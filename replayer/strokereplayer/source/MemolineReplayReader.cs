using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;

namespace StrokeReplay;

/// <summary>
/// Uses Recognizer's CRC/Brotli reader, then resolves only canvas packages and pen operations.
/// State results are placed after their reserved hardware anchor, regardless of append order.
/// </summary>
internal static class MemolineReplayReader
{
    private sealed record ViewSlot(ulong Anchor, ulong AppendId, CanvasViewSnapshot? View, string? Error);
    private sealed class PenOperation(ulong id, JsonElement begin)
    {
        public ulong Id { get; } = id;
        public JsonElement Begin { get; } = begin;
        public List<JsonElement> Frames { get; } = [begin];
        public long? EndTicks { get; set; }
    }

    public static MemolineReplayDocument Read(string path)
        => Parse(Path.GetFullPath(path), MemolineReader.Read(path));

    internal static MemolineReplayDocument Parse(string path, IEnumerable<JsonElement> records, bool allowEmpty = false)
    {
        // Keep only replay metadata. Screenshot blobs can be tens of MiB per frame.
        var relevant = records.Where(IsRelevant).ToArray();
        var headers = relevant.Where(f => Kind(f) == "header").ToArray();
        if (headers.Length != 1)
            throw new InvalidDataException("memoline 必须包含唯一的会话头。");
        var header = headers[0];
        if (!header.TryGetProperty("frequency", out var clock) || !clock.TryGetInt64(out long frequency) || frequency <= 0)
            throw new InvalidDataException("memoline 缺少有效的 Stopwatch frequency，无法还原采样时间。");
        string sessionId = Text(header, "sessionId") ?? throw new InvalidDataException("memoline 缺少 sessionId。");

        var hardware = relevant.Where(IsHardware).OrderBy(f => RequiredUnsigned(f, "eventId")).ToArray();
        var ids = new HashSet<ulong>();
        long previousTicks = -1;
        foreach (var frame in hardware)
        {
            ulong id = RequiredUnsigned(frame, "eventId");
            long ticks = RequiredTicks(frame);
            if (id == 0 || !ids.Add(id) || ticks < previousTicks)
                throw new InvalidDataException("memoline 硬件事件 ID 重复、为零或发生时间倒退。");
            previousTicks = ticks;
        }

        var slots = ResolveViews(relevant, ids);
        var operations = ReadPenOperations(relevant, hardware);
        var navigationOperations = FindNavigationOperations(relevant);
        var deviceLimits = ReadDeviceLimits(relevant);
        var initialization = relevant.Where(f => Kind(f) == "initializationConfiguration")
            .OrderBy(f => RequiredUnsigned(f, "appendId")).ToArray();
        var strokes = new List<MemolineReplayStroke>();
        int skippedNavigation = 0, skippedOutside = 0, skippedInvalidView = 0, slotIndex = 0;
        CanvasViewSnapshot? view = null;
        foreach (var operation in operations.OrderBy(o => RequiredUnsigned(o.Begin, "eventId")))
        {
            ulong firstEventId = RequiredUnsigned(operation.Begin, "eventId");
            while (slotIndex < slots.Length && slots[slotIndex].Anchor < firstEventId)
            {
                var slot = slots[slotIndex++];
                view = slot.View;
            }
            if (navigationOperations.Contains(operation.Id))
            {
                skippedNavigation++;
                continue;
            }
            if (view is null)
            {
                skippedInvalidView++;
                continue;
            }
            var startData = operation.Begin.GetProperty("data");
            var start = Coordinates(startData, operation.Id);
            if (view.Viewport is { } viewport &&
                (start.X < viewport.Left || start.X >= viewport.Right || start.Y < viewport.Top || start.Y >= viewport.Bottom))
            {
                skippedOutside++;
                continue;
            }
            var configuration = initialization.LastOrDefault(f => RequiredUnsigned(f, "appendId") < RequiredUnsigned(operation.Begin, "appendId"));
            var strokeView = configuration.ValueKind == JsonValueKind.Undefined
                ? view : view.WithInitializationConfiguration(configuration.GetProperty("data"));

            long startTicks = RequiredTicks(operation.Begin);
            long lastTicks = startTicks;
            var samples = new List<MemolineReplaySample>();
            foreach (var frame in operation.Frames)
            {
                long ticks = RequiredTicks(frame);
                if (ticks < lastTicks)
                    throw new InvalidDataException($"笔操作 {operation.Id} 的采样时间倒退。");
                lastTicks = ticks;
                var data = frame.GetProperty("data");
                bool contact = Kind(frame) != "penEnd";
                if (contact && Text(data, "contactState") is { } contactState && contactState != "Contact")
                    throw new InvalidDataException($"笔操作 {operation.Id} 包含非接触的落笔/采样记录。");
                var coordinates = Coordinates(data, operation.Id);
                double pressure = contact ? Pressure(frame, deviceLimits, operation.Id) : 0;
                samples.Add(new(ticks, coordinates.X, coordinates.Y, pressure,
                    OptionalNumber(data, "tiltX", 0), OptionalNumber(data, "tiltY", 0), contact,
                    RequiredUnsigned(frame, "appendId"), RequiredUnsigned(frame, "eventId")));
            }
            long endTicks = operation.EndTicks ?? throw new InvalidDataException(
                $"笔操作 {operation.Id} 尚未抬笔或中断；请先结束录制，再重放该 memoline。");
            if (endTicks < lastTicks)
                throw new InvalidDataException($"笔操作 {operation.Id} 的结束时间早于最后采样。");
            if (samples[^1].InContact)
            {
                // penInterrupted carries no coordinates. Release at the last measured position.
                samples.Add(samples[^1] with { Ticks = endTicks, Pressure = 0, InContact = false, AppendId = 0, EventId = 0 });
            }
            strokes.Add(new(operation.Id, startTicks, endTicks, strokeView, samples));
        }
        if (strokes.Count == 0 && !allowEmpty)
            throw new InvalidDataException($"memoline 没有可重放的绘画笔操作（跳过 {skippedNavigation} 个空格导航、{skippedOutside} 个画布外操作、{skippedInvalidView} 个无效画布视图操作）。");
        return new(path, sessionId, frequency, strokes, skippedNavigation, skippedOutside, skippedInvalidView);
    }

    private static bool IsRelevant(JsonElement frame)
        => IsHardware(frame) || Kind(frame) is "header" or "statePackageReserved" or "statePackageResult"
            or "penInterrupted" or "tabletDeviceChanged" or "driverConfiguration" or "panelUpdateRequested" or "initializationConfiguration" or "keyboardStateChanged" ||
            Kind(frame) == "coreStateUpdated" && Text(frame.GetProperty("data"), "module") == "canvasViewState";

    private static bool IsHardware(JsonElement frame) => Text(frame, "path") == "hardware";

    private static ViewSlot[] ResolveViews(JsonElement[] frames, HashSet<ulong> hardwareIds)
    {
        var reservations = UniquePackages(frames, "statePackageReserved");
        var results = UniquePackages(frames, "statePackageResult");
        foreach (string id in results.Keys)
            if (!reservations.ContainsKey(id))
                throw new InvalidDataException($"状态结果 {id} 没有 statePackageReserved 因果位置。");
        var cores = frames.Where(f => Kind(f) == "coreStateUpdated" && Text(f.GetProperty("data"), "packageId") is not null)
            .GroupBy(f => Text(f.GetProperty("data"), "packageId")!)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => RequiredUnsigned(f, "appendId")).Last().GetProperty("data"));
        var slots = new List<ViewSlot>();
        foreach (var (id, reservation) in reservations)
        {
            var data = reservation.GetProperty("data");
            ulong anchor = RequiredUnsigned(data, "afterEventId");
            ulong reservationOrder = data.TryGetProperty("reservationOrder", out var order)
                ? order.GetUInt64() : RequiredUnsigned(reservation, "appendId");
            if (anchor != 0 && !hardwareIds.Contains(anchor))
                throw new InvalidDataException($"状态包 {id} 的硬件锚点 {anchor} 不存在。");
            JsonElement update = default, raw = default;
            bool resolved = results.TryGetValue(id, out var resultFrame);
            if (resolved)
            {
                var resultData = resultFrame.GetProperty("data");
                if (RequiredUnsigned(resultData, "afterEventId") != anchor)
                    throw new InvalidDataException($"状态包 {id} 的结果与预留锚点不一致。");
                var result = resultData.GetProperty("result");
                if (result.TryGetProperty("updates", out var updates) && updates.ValueKind == JsonValueKind.Array)
                {
                    var canvas = updates.EnumerateArray().Where(u => Text(u, "module") == "canvasViewState").ToArray();
                    if (canvas.Length > 1) throw new InvalidDataException($"状态包 {id} 重复提供 canvasViewState。");
                    if (canvas.Length == 1) update = canvas[0];
                }
            }
            if (cores.TryGetValue(id, out var core))
            {
                // A resolved canvas core is usable even while unrelated package cores are pending.
                if (!resolved && update.ValueKind == JsonValueKind.Undefined) update = core;
                if (core.TryGetProperty("rawResult", out var rawResult)) raw = rawResult;
            }
            if (update.ValueKind != JsonValueKind.Undefined)
            {
                var parsed = CanvasViewSnapshot.TryParse(update, raw, out var view);
                slots.Add(new(anchor, reservationOrder, parsed ? view : null,
                    parsed ? null : $"canvasViewState 状态为 {Text(update, "status") ?? "无效"}（package {id}）"));
            }
            else if (!resolved && (anchor == 0 || CanvasWasRequested(frames, anchor)))
                slots.Add(new(anchor, reservationOrder, null, $"画布状态包 {id} 尚未完成"));
        }
        return slots.OrderBy(s => s.Anchor).ThenBy(s => s.AppendId).ToArray();
    }

    private static Dictionary<string, JsonElement> UniquePackages(JsonElement[] frames, string kind)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var frame in frames.Where(f => Kind(f) == kind))
        {
            string id = Text(frame.GetProperty("data"), "packageId") ?? throw new InvalidDataException("状态包缺少 packageId。");
            if (!result.TryAdd(id, frame)) throw new InvalidDataException($"状态包 {id} 重复记录 {kind}。");
        }
        return result;
    }

    private static bool CanvasWasRequested(JsonElement[] frames, ulong anchor)
        => frames.Any(f => Kind(f) == "panelUpdateRequested" &&
            f.TryGetProperty("relatedEventIds", out var refs) && refs.ValueKind == JsonValueKind.Array &&
            refs.EnumerateArray().Any(r => r.TryGetUInt64(out var id) && id == anchor) &&
            f.GetProperty("data").TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.Array &&
            modules.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "canvasViewState"));

    private static List<PenOperation> ReadPenOperations(JsonElement[] frames, JsonElement[] hardware)
    {
        var operations = new Dictionary<ulong, PenOperation>();
        var pointOperations = new Dictionary<ulong, PenOperation>();
        foreach (var frame in hardware)
        {
            string kind = Kind(frame);
            if (kind is not ("penBegin" or "penSample" or "penEnd")) continue;
            if (!frame.TryGetProperty("deviceSource", out var source) || Text(source, "deviceType") != "pen")
                throw new InvalidDataException("memoline 笔事件缺少 pen 设备来源。");
            ulong eventId = RequiredUnsigned(frame, "eventId");
            ulong operationId = frame.TryGetProperty("operationId", out var operation) && operation.TryGetUInt64(out var id) ? id : eventId;
            if (kind == "penBegin")
            {
                if (!operations.TryAdd(operationId, new(operationId, frame)))
                    throw new InvalidDataException($"笔操作 {operationId} 重复落笔。");
            }
            else
            {
                if (!operations.TryGetValue(operationId, out var active))
                    throw new InvalidDataException($"笔事件 {eventId} 找不到落笔操作 {operationId}。");
                if (active.EndTicks is not null) throw new InvalidDataException($"笔操作 {operationId} 在抬笔后继续采样。");
                active.Frames.Add(frame);
                if (kind == "penEnd") active.EndTicks = RequiredTicks(frame);
            }
            pointOperations.Add(eventId, operations[operationId]);
        }
        foreach (var interruption in frames.Where(f => Kind(f) == "penInterrupted"))
        {
            if (!interruption.TryGetProperty("relatedEventIds", out var refs) || refs.ValueKind != JsonValueKind.Array) continue;
            var interrupted = refs.EnumerateArray().Select(r => r.GetUInt64())
                .Where(pointOperations.ContainsKey).Select(id => pointOperations[id]).Distinct().ToArray();
            if (interrupted.Length > 1) throw new InvalidDataException("penInterrupted 同时引用多个笔操作。");
            if (interrupted.Length == 1)
            {
                if (interrupted[0].EndTicks is not null) throw new InvalidDataException($"笔操作 {interrupted[0].Id} 重复结束。");
                interrupted[0].EndTicks = RequiredTicks(interruption);
            }
        }
        return operations.Values.ToList();
    }

    private sealed record DeviceLimit(ulong AppendId, string DeviceId, double MaxPressure);
    private static DeviceLimit[] ReadDeviceLimits(JsonElement[] frames)
    {
        var limits = new List<DeviceLimit>();
        foreach (var frame in frames.Where(f => Kind(f) is "tabletDeviceChanged" or "driverConfiguration"))
        {
            var data = frame.GetProperty("data");
            if (Kind(frame) == "driverConfiguration" && data.TryGetProperty("device", out var device)) data = device;
            if (Text(data, "deviceId") is { } id && Number(data, "maxPressure") is { } max && max > 0)
                limits.Add(new(RequiredUnsigned(frame, "appendId"), id, max));
        }
        return limits.OrderBy(l => l.AppendId).ToArray();
    }

    private static double Pressure(JsonElement frame, DeviceLimit[] limits, ulong operationId)
    {
        var data = frame.GetProperty("data");
        if (Number(data, "normalizedPressure") is { } normalized)
            return ValidatePressure(normalized, operationId);
        // Recognizer's mappedPressure still uses the device's hardware pressure range.
        // Dividing by maxPressure converts units; the driver curve must not run again.
        if ((Number(data, "mappedPressure") ?? Number(data, "pressure")) is not { } raw)
            throw new InvalidDataException($"笔操作 {operationId} 缺少压感。Windows 被动笔记录没有足够数据用于数位板重放。");
        double? maxPressure = Number(data, "maxPressure");
        if (maxPressure is not > 0)
        {
            string? deviceId = Text(data, "deviceId");
            if (deviceId is null && frame.TryGetProperty("deviceSource", out var source)) deviceId = Text(source, "deviceId");
            ulong appendId = RequiredUnsigned(frame, "appendId");
            maxPressure = limits.LastOrDefault(l => l.AppendId < appendId && l.DeviceId == deviceId)?.MaxPressure;
        }
        if (maxPressure is not > 0)
            throw new InvalidDataException($"笔操作 {operationId} 只有原始压感，却没有设备 maxPressure。无法可靠归一化。");
        return ValidatePressure(raw / maxPressure.Value, operationId);
    }

    private static double ValidatePressure(double value, ulong operationId)
        => value >= 0 && value <= 1 && double.IsFinite(value) ? value :
            throw new InvalidDataException($"笔操作 {operationId} 的归一化压感超出 [0,1]。");

    private static (double X, double Y) Coordinates(JsonElement data, ulong operationId)
    {
        if (Number(data, "x") is { } x && Number(data, "y") is { } y) return (x, y);
        if (Number(data, "screenX") is { } sx && Number(data, "screenY") is { } sy) return (sx, sy);
        throw new InvalidDataException($"笔操作 {operationId} 缺少 Windows 屏幕坐标。原始 tabletX/tabletY 不能直接作为屏幕像素重放。");
    }

    private static HashSet<ulong> FindNavigationOperations(JsonElement[] frames)
    {
        var navigation = new HashSet<ulong>();
        var active = new HashSet<ulong>();
        var pointOperations = new Dictionary<ulong, ulong>();
        bool navigationHeld = false;
        foreach (var frame in frames.Where(f => Kind(f) != "header").OrderBy(f => RequiredUnsigned(f, "appendId")))
        {
            string kind = Kind(frame);
            var data = frame.GetProperty("data");
            if (kind is "keyboardStateChanged" or "keyInput")
            {
                if (data.TryGetProperty("heldKeys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                    navigationHeld = keys.EnumerateArray().Any(IsNavigationKey);
                else if (kind == "keyboardStateChanged" && Text(data, "action") == "reset")
                    navigationHeld = false;
            }
            if (kind is "penBegin" or "penSample" or "penEnd")
            {
                ulong eventId = RequiredUnsigned(frame, "eventId");
                ulong operationId = frame.TryGetProperty("operationId", out var operation) && operation.TryGetUInt64(out var id)
                    ? id : eventId;
                pointOperations[eventId] = operationId;
                if (kind == "penBegin") active.Add(operationId);
                bool penNavigation = data.TryGetProperty("heldKeys", out var penKeys) && penKeys.ValueKind == JsonValueKind.Array
                    ? penKeys.EnumerateArray().Any(IsNavigationKey) : navigationHeld;
                if (penNavigation) navigation.Add(operationId);
                if (kind == "penEnd") active.Remove(operationId);
            }
            else if (kind is "keyboardStateChanged" or "keyInput")
            {
                if (navigationHeld) navigation.UnionWith(active);
            }
            else if (kind == "penInterrupted" && frame.TryGetProperty("relatedEventIds", out var refs)
                && refs.ValueKind == JsonValueKind.Array)
            {
                foreach (var reference in refs.EnumerateArray())
                    if (reference.TryGetUInt64(out ulong pointId) && pointOperations.TryGetValue(pointId, out ulong operationId))
                        active.Remove(operationId);
            }
        }
        return navigation;
    }

    private static bool IsNavigationKey(JsonElement key)
    {
        if (key.ValueKind == JsonValueKind.Number && key.TryGetInt32(out int vk)) return vk is 0x20 or 0x52;
        if (key.ValueKind != JsonValueKind.String) return false;
        return key.GetString()?.ToUpperInvariant() is "SPACE" or "SPACEBAR" or "VK_SPACE" or "R" or "VK_R";
    }

    private static string Kind(JsonElement frame) => Text(frame, "kind") ?? throw new InvalidDataException("memoline 记录缺少 kind。");
    private static string? Text(JsonElement data, string field)
        => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static double? Number(JsonElement data, string field)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
            throw new InvalidDataException($"memoline 的 {field} 不是有限数值。");
        return number;
    }
    private static double OptionalNumber(JsonElement data, string field, double fallback) => Number(data, field) ?? fallback;
    private static ulong RequiredUnsigned(JsonElement data, string field)
        => data.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var id) ? id :
            throw new InvalidDataException($"memoline 缺少有效的 {field}。");
    private static long RequiredTicks(JsonElement frame)
        => frame.TryGetProperty("ticks", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var ticks) && ticks >= 0 ? ticks :
            throw new InvalidDataException("memoline 缺少有效的事件 ticks。");
}


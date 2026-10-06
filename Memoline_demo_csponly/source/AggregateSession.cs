using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using CanvasLayerWatcher;

namespace MemolineDemo;

// This stream supplements the recorder. It never edits, reconstructs, or writes
// a mechanical frame: every pointer is resolved from the recorder's own file.
internal sealed class AggregateSession : IDisposable
{
    private const int AssetChunkBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Json = new(MemolineWriter.Json);
    private sealed record NativePointer(ulong AppendId, ulong EventId, ulong? OperationId, long Ticks, bool Hardware, string? Kind,
        PenDownLocation? PenDownLocation = null);
    private sealed class Boundary(ViewportObservation observation, long from, bool recordingEnd = false)
    {
        public ViewportObservation Observation { get; } = observation;
        public long FromTicks { get; } = from;
        public long TriggerTicks => Observation.TriggerTicks;
        public long ToTicks => EndTicks ?? TriggerTicks;
        public long? EndTicks { get; set; }
        public bool RecordingEnd { get; } = recordingEnd;
        public (CaptureRequest Request, SnapshotUpdate Update)? PendingCapture { get; set; }
        public bool Queued { get; set; }
        public bool Committed { get; set; }
        public string? Failure { get; set; }
    }
    private readonly object _sync = new();
    private readonly StreamWriter _output;
    private readonly FileStream _stream;
    private MemolineReadSession? _native;
    private readonly Dictionary<ulong, NativePointer> _byAppend = [];
    private readonly Dictionary<ulong, NativePointer> _byEvent = [];
    private readonly Dictionary<string, string> _assetIdsByHash = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, Boundary> _boundaries = [];
    private readonly long _frequency, _originTicks;
    private long _lastObservation = -1, _lastBoundary;
    private long _nativeEndTicks = -1;
    private Boundary? _recordingEnd;
    private string? _recordingEndFailure;
    private bool _committingEnd;
    private ulong _lastAppend;
    private PanelRegionMap _nativeRegions = PanelRegionMap.Unavailable();
    private bool _nativeHeader, _disposed;
    private int _samples, _packets, _assets;
    public string FilePath { get; }
    public string MechanicalPath { get; }
    public string SessionId { get; }
    public bool IsComplete { get; private set; }

    public AggregateSession(string aggregatePath, string mechanicalPath, RecorderRealtimeEvent hello)
    {
        if (hello.Kind != "hello" || string.IsNullOrWhiteSpace(hello.SessionId))
            throw new InvalidDataException("聚集文件需要 Recognizer 的原生会话 hello");
        SessionId = hello.SessionId;
        _frequency = J.Tick(hello.Data, "frequency", 0);
        _originTicks = J.Tick(hello.Data, "originTicks", -1);
        if (_frequency <= 0 || _originTicks < 0) throw new InvalidDataException("Recognizer 时钟元数据不完整");
        FilePath = Path.GetFullPath(aggregatePath);
        MechanicalPath = Path.GetFullPath(mechanicalPath);
        if (FilePath.Equals(MechanicalPath, StringComparison.OrdinalIgnoreCase)
            || FilePath.Equals(MechanicalPath + ".part", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("聚集文件必须与机械事件文件分开");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _stream = new(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _output = new(_stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        try
        {
            Write(new { kind = "header", schema = "memoline-aggregate/v1", SessionId, frequency = _frequency,
                originTicks = _originTicks, mechanicalFile = Path.GetFileName(MechanicalPath), mechanicalPath = MechanicalPath,
                algorithmVersion = "csponly-dimensions/v1", pointerModel = "nativeAppendId",
                dimensionsMutable = true, packetsImmutable = true, endPacketPolicy = "nativeFooterClosure/v1",
                assetStorage = "sha256Deduplication/v1" });
        }
        catch { _output.Dispose(); _stream.Dispose(); throw; }
    }

    public void ObserveViewport(ViewportObservation observation)
    {
        lock (_sync)
        {
            Active();
            // Same-session reconnects replay confirmed snapshots. They do not
            // create a second sample or mutate a previously observed boundary.
            if (observation.Message.SessionId == SessionId && (observation.IsBaseline || observation.Message.IsSnapshot)
                && observation.TriggerTicks <= _lastObservation) return;
            if (observation.Message.SessionId != SessionId || observation.TriggerTicks < 0
                || observation.Changed is not (0 or 1) || observation.TriggerTicks <= _lastObservation)
                throw new InvalidDataException("视口观测必须属于同一会话并按 triggerTicks 单向排列");
            if (observation.IsBaseline && observation.Changed != 0)
                throw new InvalidDataException("视口基线的变化值必须为 0");
            Write(new { kind = "dimensionSample", SessionId, ticks = observation.TriggerTicks,
                canvasViewportChanged = observation.Changed, actionPatternContinuity = (double?)null,
                toolContinuity = (double?)null, layerContinuity = (double?)null,
                regionContinuity = (double?)null, timeContinuity = (double?)null,
                algorithmVersion = "csponly-dimensions/v1", observation.IsBaseline,
                observation.HasEditingInput, observationAppendId = observation.Message.AppendId });
            _samples++;
            _lastObservation = observation.TriggerTicks;
            if (observation.Changed == 1 && _recordingEnd is null)
            {
                _boundaries.Add(observation.TriggerTicks, new(observation, _lastBoundary));
                _lastBoundary = observation.TriggerTicks;
            }
        }
    }

    public void ObserveRecordingEnd(ViewportObservation observation)
    {
        lock (_sync)
        {
            Active();
            if (observation.Message.SessionId != SessionId || observation.Message.Kind != "recordingEndRequested"
                || observation.Message.AppendId == 0 || observation.TriggerTicks != observation.Message.Ticks
                || observation.TriggerTicks < _lastBoundary)
                throw new InvalidDataException("结束边界必须来自同一原生会话，且不早于最后一个视口边界。");
            if (_recordingEnd is { } previous)
            {
                if (previous.Observation.Message.AppendId != observation.Message.AppendId)
                    throw new InvalidDataException("同一会话不能建立两个结束边界。");
                return;
            }
            _recordingEnd = new(observation, _lastBoundary, recordingEnd: true);
        }
    }

    public void CaptureQueued(CaptureRequest request)
    {
        lock (_sync)
        {
            Active();
            var boundary = Find(request);
            if (boundary.Committed) return;
            boundary.Queued = true;
            boundary.Failure = null;
        }
    }

    public void CaptureCommitted(CaptureRequest request, SnapshotUpdate update)
    {
        lock (_sync)
        {
            Active();
            var boundary = Find(request);
            if (boundary.Committed) return;
            if (boundary.RecordingEnd && !_committingEnd)
            {
                // Its pixels are fixed now. Publish only after the native footer
                // so the packet also owns late input and interrupted contacts.
                boundary.PendingCapture = (request, update);
                return;
            }
            try
            {
                // A realtime delivery precedes the disk flush. Wait for this
                // exact native frame, never infer it from transport Sequence.
                WaitForObservation(boundary);
                string root = Path.GetFullPath(update.PacketDirectory);
                string manifestPath = CheckedAssetPath(root, "manifest.json", requirePng: false);
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var manifest = document.RootElement;
                ValidateManifest(manifest, boundary.TriggerTicks);
                var labels = Array(manifest, "labels");
                var images = Array(manifest, "images");
                var recognizer = J.Get(manifest, "recognizer");
                var inputs = Array(recognizer, "inputs");
                var states = Array(recognizer, "states");
                string packetId = PacketId(boundary);
                var paths = AssetPaths(manifest, images).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
                // Validate every reference before the first asset is appended.
                var assets = paths.Select(relative => (Relative: relative,
                    Path: CheckedAssetPath(root, relative, requirePng: true), Id: packetId + "/" + relative.Replace('\\', '/'))).ToArray();
                long diffFromTicks = J.Tick(recognizer, "fromTicks", -1);
                var matrices = labels.Select(label => Matrix(label, inputs, states, boundary, diffFromTicks)).ToArray();
                var imageAssets = assets.Select(a => new { path = a.Relative, assetId = AppendAsset(a.Path, a.Id) }).ToArray();
                Write(new { kind = "packet", SessionId, id = packetId, fromTicks = boundary.FromTicks,
                    toTicks = boundary.ToTicks, triggerTicks = boundary.TriggerTicks,
                    boundaryKind = boundary.RecordingEnd ? "recordingEnd" : "canvasViewportChanged",
                    observationAppendId = boundary.Observation.Message.AppendId,
                    status = update.Baseline ? "baseline" : "captured", reason = update.Baseline ? "firstLayerSnapshot" : (string?)null,
                    eventPointers = WindowPointers(boundary), penContacts = WindowPenContacts(boundary), dirtyMatrices = matrices,
                    imageAssets,
                    manifest });
                boundary.Committed = true;
                _packets++;
            }
            catch (Exception ex)
            {
                boundary.Failure = "captureCommitFailed: " + ex.Message;
                throw;
            }
        }
    }

    public void CaptureFailed(CaptureRequest request, string reason)
    {
        lock (_sync)
        {
            Active();
            var boundary = Find(request);
            if (!boundary.Committed) boundary.Failure = string.IsNullOrWhiteSpace(reason) ? "captureFailed" : reason;
        }
    }

    public void RecordingEndFailed(string reason)
    {
        lock (_sync)
        {
            Active();
            _recordingEndFailure = reason;
            if (_recordingEnd is { Committed: false } boundary) boundary.Failure = reason;
        }
    }

    // Only called after the recorder has stopped gracefully. A footer proves
    // the complete mechanical prefix exists, including late core results.
    public void Complete()
    {
        lock (_sync)
        {
            if (IsComplete) return;
            Active();
            RefreshNative();
            if (_native is null || !_native.IsComplete || !_nativeHeader)
                throw new InvalidDataException("Recognizer 原生机械文件尚未正常写入 footer，不能封盘");
            foreach (var boundary in _boundaries.Values.Where(b => !b.Committed))
            {
                WaitForObservation(boundary);
                string reason = boundary.Failure ?? (!boundary.Observation.HasEditingInput
                    ? "noEditingInput" : boundary.Queued ? "captureNotCompletedBeforeSeal" : "layerUnconfirmedOrCaptureCoalesced");
                EmptyPacket(boundary, reason);
            }
            if (_nativeEndTicks < _lastBoundary) throw new InvalidDataException("原生 footer 早于最后一个聚集边界。");
            if (_recordingEnd is null)
            {
                var footer = new RecorderRealtimeEvent(1, SessionId, 0, "system", "sessionFooter", false,
                    0, 0, _nativeEndTicks, _nativeEndTicks, _nativeEndTicks, null, null, null,
                    JsonSerializer.SerializeToElement(new { boundaryKind = "recordingEnd" }));
                _recordingEnd = new(new(footer, _nativeEndTicks, 0, false, false), _lastBoundary, recordingEnd: true);
                _recordingEnd.Failure = _recordingEndFailure ?? "recordingEndCaptureUnavailable";
            }
            _recordingEnd.EndTicks = _nativeEndTicks;
            if (_recordingEnd.PendingCapture is { } pending)
            {
                _committingEnd = true;
                try { CaptureCommitted(pending.Request, pending.Update); }
                catch (Exception) { /* CaptureCommitted retained the concrete failure. */ }
                finally { _committingEnd = false; }
            }
            if (!_recordingEnd.Committed)
            {
                WaitForObservation(_recordingEnd);
                EmptyPacket(_recordingEnd, _recordingEnd.Failure ?? "recordingEndLayerOrCanvasUnconfirmed");
            }
            _lastBoundary = _nativeEndTicks;
            Write(new { kind = "footer", SessionId, dimensionSamples = _samples, packets = _packets,
                assetChunks = _assets, lastBoundaryTicks = _lastBoundary, nativeLastAppendId = _lastAppend });
            IsComplete = true;
        }
    }

    private void EmptyPacket(Boundary boundary, string reason)
    {
        Write(new { kind = "packet", SessionId, id = PacketId(boundary), fromTicks = boundary.FromTicks,
            toTicks = boundary.ToTicks, triggerTicks = boundary.TriggerTicks,
            boundaryKind = boundary.RecordingEnd ? "recordingEnd" : "canvasViewportChanged",
            observationAppendId = boundary.Observation.Message.AppendId, status = "empty", reason,
            eventPointers = WindowPointers(boundary), penContacts = WindowPenContacts(boundary), dirtyMatrices = System.Array.Empty<object>(),
            imageAssets = System.Array.Empty<object>(), manifest = (object?)null });
        boundary.Committed = true;
        _packets++;
    }

    private object Matrix(JsonElement label, JsonElement[] inputs, JsonElement[] states, Boundary boundary, long diffFromTicks)
    {
        long from = J.Tick(label, "fromTicks", boundary.FromTicks), to = J.Tick(label, "toTicks", boundary.ToTicks);
        long? operation = J.Get(label, "operationId") is { ValueKind: JsonValueKind.Number } op && op.TryGetInt64(out long value) ? value : null;
        bool unresolved = operation is null || operation <= 0 || J.Text(label, "source") == "low-resolution-fallback";
        var pointers = new Dictionary<ulong, object>();
        if (!unresolved)
            foreach (var input in inputs)
            {
                long ticks = J.Tick(input, "ticks", -1);
                bool context = J.Get(input, "continuity").ValueKind == JsonValueKind.True;
                if (J.Tick(input, "operationId", -1) != operation || ticks > to || ticks > boundary.ToTicks
                    || (!context && (ticks < from || ticks <= diffFromTicks))) continue;
                var native = ResolveInput(input);
                if (native is null || !native.Hardware || native.Ticks != ticks) continue;
                bool anchor = native.OperationId is null && native.EventId == (ulong)operation!.Value
                    && native.Kind is "penBegin" or "mouseDown";
                if (native.OperationId != (ulong)operation!.Value && !anchor) continue;
                pointers.TryAdd(native.AppendId, Pointer(native, context || ticks <= boundary.FromTicks));
            }
        var stateIds = Array(label, "stateIds").Select(v => v.GetString()).ToHashSet(StringComparer.Ordinal);
        var statePointers = new Dictionary<ulong, object>();
        foreach (var state in states.Where(s => stateIds.Contains(J.Text(s, "id"))))
        {
            ulong appendId = Unsigned(state, "appendId");
            if (appendId > 0 && _byAppend.TryGetValue(appendId, out var native) && !native.Hardware && native.Ticks <= boundary.ToTicks)
                statePointers.TryAdd(appendId, Pointer(native, context: true));
        }
        return new { id = J.Text(label, "id"), label, impactRange = Required(label, "impactRange"),
            imageIds = Array(label, "imageIds"), attribution = unresolved ? "unresolved" : pointers.Count > 0 ? "matchedNativeOperation" : "unresolved",
            eventPointers = pointers.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray(),
            statePointers = statePointers.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray() };
    }

    private NativePointer? ResolveInput(JsonElement input)
    {
        ulong appendId = Unsigned(input, "appendId");
        if (appendId > 0) return _byAppend.GetValueOrDefault(appendId);
        ulong eventId = Unsigned(input, "eventId");
        return eventId > 0 ? _byEvent.GetValueOrDefault(eventId) : null;
    }
    private object[] WindowPointers(Boundary boundary) => _byAppend.Values
        .Where(p => p.Hardware && p.Ticks > boundary.FromTicks && p.Ticks <= boundary.ToTicks)
        .OrderBy(p => p.Ticks).ThenBy(p => p.AppendId).Select(p => Pointer(p, context: false)).ToArray();
    private object[] WindowPenContacts(Boundary boundary)
    {
        var operations = _byAppend.Values.Where(p => p.Hardware && p.Ticks > boundary.FromTicks && p.Ticks <= boundary.ToTicks
            && p.Kind is "penBegin" or "penSample" or "penEnd")
            .Select(p => p.OperationId ?? p.EventId).ToHashSet();
        return _byAppend.Values.Where(p => p.Hardware && p.Kind == "penBegin" && p.Ticks <= boundary.ToTicks
                && operations.Contains(p.OperationId ?? p.EventId))
            .OrderBy(p => p.Ticks).ThenBy(p => p.AppendId)
            .Select(p => (object)new { operationId = p.OperationId ?? p.EventId,
                beginEventPointer = Pointer(p, context: p.Ticks <= boundary.FromTicks), p.PenDownLocation }).ToArray();
    }
    private object Pointer(NativePointer p, bool context) => new { SessionId, p.AppendId, p.EventId, p.OperationId, p.Ticks, context };
    private Boundary Find(CaptureRequest request)
    {
        if (request.Context is { } context && context.SessionId != SessionId)
            throw new InvalidDataException("保存任务属于另一个 Recognizer 会话");
        return request.TriggerKind == "recordingEnd" && _recordingEnd?.TriggerTicks == request.TriggerTicks ? _recordingEnd
            : _boundaries.GetValueOrDefault(request.TriggerTicks)
            ?? throw new InvalidDataException("保存任务没有已确认的画布变化边界");
    }
    private string PacketId(Boundary boundary) => (boundary.RecordingEnd ? "recording-end-" : "viewport-")
        + boundary.TriggerTicks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture);

    private void WaitForObservation(Boundary boundary)
    {
        ulong appendId = boundary.Observation.Message.AppendId;
        if (appendId == 0 && boundary.RecordingEnd && boundary.TriggerTicks == _nativeEndTicks && _native?.IsComplete == true) return;
        if (appendId == 0) throw new InvalidDataException("确认的画布变化缺少原生 appendId；不能用实时 sequence 替代");
        var wait = Stopwatch.StartNew();
        while (true)
        {
            RefreshNative();
            if (_byAppend.TryGetValue(appendId, out var pointer))
            {
                if (pointer.Hardware || pointer.Ticks != boundary.Observation.Message.Ticks)
                    throw new InvalidDataException("画布观测与原生机械记录不匹配");
                return;
            }
            if (_native?.IsComplete == true || wait.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("等待画布观测的原生完整写入前缀失败，appendId=" + appendId);
            Thread.Sleep(20);
        }
    }
    private void RefreshNative()
    {
        if (_native is null)
        {
            if (!File.Exists(MechanicalPath) && !File.Exists(MechanicalPath + ".part")) return;
            _native = MemolineReader.Open(MechanicalPath);
        }
        while (true)
        {
            var frames = _native.ReadAvailable(256);
            if (frames.Count == 0) break;
            foreach (var frame in frames)
            {
                string? kind = J.Text(frame, "kind");
                if (kind == "header")
                {
                    if (_nativeHeader || J.Text(frame, "sessionId") != SessionId || J.Tick(frame, "frequency", 0) != _frequency
                        || J.Tick(frame, "originTicks", -1) != _originTicks)
                        throw new InvalidDataException("机械记录的会话或时钟与实时 hello 不匹配");
                    _nativeHeader = true;
                    continue;
                }
                if (kind == "footer") { _nativeEndTicks = J.Tick(frame, "ticks", -1); continue; }
                ulong appendId = Unsigned(frame, "appendId");
                if (!_nativeHeader || appendId == 0 || appendId != _lastAppend + 1)
                    throw new InvalidDataException("机械记录 appendId 不构成统一完整前缀");
                _lastAppend = appendId;
                ulong eventId = Unsigned(frame, "eventId");
                var op = J.Get(frame, "operationId");
                var data = J.Get(frame, "data");
                long ticks = J.Tick(frame, "ticks", -1);
                if (kind == "workspaceStatus") _nativeRegions = PanelRegionMap.FromWorkspaceStatus(data, appendId, ticks);
                PenDownLocation? location = kind == "penBegin" ? PanelRegionMap.ReadLocation(data) : null;
                if (kind == "penBegin" && location is null && _nativeRegions.HasRegions
                    && J.Number(data, "x") is { } x && J.Number(data, "y") is { } y) location = _nativeRegions.Classify(x, y);
                var pointer = new NativePointer(appendId, eventId,
                    op.ValueKind == JsonValueKind.Number && op.TryGetUInt64(out ulong operation) ? operation : null,
                    ticks, J.Text(frame, "path") == "hardware", kind, location);
                if (pointer.Ticks < 0 || (pointer.Hardware && eventId == 0)) throw new InvalidDataException("原生事件指针字段无效");
                _byAppend.Add(appendId, pointer);
                if (pointer.Hardware) _byEvent.Add(eventId, pointer);
                // frame.Data (including screenshots) is intentionally not retained.
            }
        }
    }

    private void ValidateManifest(JsonElement manifest, long triggerTicks)
    {
        var recognizer = Required(manifest, "recognizer");
        long from = J.Tick(recognizer, "fromTicks", -1);
        if (J.Text(recognizer, "sessionId") != SessionId || J.Tick(recognizer, "frequency", 0) != _frequency
            || J.Tick(recognizer, "toTicks", -1) != triggerTicks
            || from < 0 || from > triggerTicks
            || J.Tick(Required(manifest, "capture"), "triggerTicks", -1) != triggerTicks)
            throw new InvalidDataException("差异 manifest 的会话、时钟或 triggerTicks 不匹配");
    }
    private static IEnumerable<string> AssetPaths(JsonElement manifest, JsonElement[] images)
    {
        foreach (var image in images)
            foreach (string key in new[] { "image", "afterImage", "nowImage", "maskImage", "differenceImage" })
                if (J.Text(image, key) is { Length: > 0 } relative) yield return relative;
        if (J.Text(manifest, "canvasPreviewImage") is { Length: > 0 } preview) yield return preview;
    }
    private static string CheckedAssetPath(string root, string relative, bool requirePng)
    {
        if (Path.IsPathRooted(relative) || string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("包内文件路径无效");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || (requirePng && !Path.GetExtension(full).Equals(".png", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("包内图像路径越界或不是 PNG");
        if (!File.Exists(full)) throw new FileNotFoundException("差异包引用的文件不存在", full);
        for (string? part = full; part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("差异包禁止链接或重解析路径");
            if (part.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return full;
    }
    private string AppendAsset(string path, string assetId)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string totalSha256 = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        if (_assetIdsByHash.TryGetValue(totalSha256, out string? existing)) return existing;
        file.Position = 0;
        int chunkCount = checked((int)Math.Max(1, (file.Length + AssetChunkBytes - 1) / AssetChunkBytes));
        var buffer = new byte[AssetChunkBytes];
        for (int index = 0; index < chunkCount; index++)
        {
            int count = 0;
            while (count < buffer.Length)
            {
                int read = file.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            Write(new { kind = "asset", SessionId, assetId, base64 = Convert.ToBase64String(buffer, 0, count),
                sha256 = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count))).ToLowerInvariant(),
                totalSha256, chunkIndex = index, chunkCount, encoding = "png", byteLength = file.Length });
            _assets++;
        }
        // Add only after all chunks are written. Each packet keeps its own
        // path-to-ID mapping; identical bytes may refer to an earlier packet.
        _assetIdsByHash.Add(totalSha256, assetId);
        return assetId;
    }
    private static JsonElement Required(JsonElement e, string key) => J.Get(e, key).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
        ? J.Get(e, key) : throw new InvalidDataException("缺少字段 " + key);
    private static JsonElement[] Array(JsonElement e, string key) => J.Get(e, key) is { ValueKind: JsonValueKind.Array } a
        ? a.EnumerateArray().ToArray() : System.Array.Empty<JsonElement>();
    private static ulong Unsigned(JsonElement e, string key) => J.Get(e, key) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetUInt64(out ulong number) ? number : 0;
    private void Active()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsComplete) throw new InvalidOperationException("聚集会话已经封盘");
    }
    private void Write(object record)
    {
        _output.WriteLine(JsonSerializer.Serialize(record, Json));
        _output.Flush();
        _stream.Flush(flushToDisk: true);
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _native?.Dispose();
            _output.Dispose();
            _stream.Dispose();
        }
    }
}

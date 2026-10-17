using PacketReplay;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemolineDemo.Editing;

/// <summary>Joins independently clocked sessions without rewriting native event identities.</summary>
internal static class ProjectEditIndex
{
    private sealed record SourceLayer(int Order, SessionSnapshot Session, LayerIndex Index,
        ImageRgba? Baseline, ImageRgba? Current, IReadOnlyList<PlanIssue> Issues);

    internal static EditIndex Build(IReadOnlyList<SessionSnapshot> prior, SessionSnapshot current,
        IReadOnlyDictionary<string, EditIndex>? sourceIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(current);
        var sessions = prior.Append(current).ToArray();
        if (sessions.Select(s => s.SessionId).Distinct(StringComparer.Ordinal).Count() != sessions.Length)
            throw new InvalidDataException("工程历史包含重复的原生 sessionId。");
        // A layer Memoline installed in place of another (event editing, precise undo) is that layer: both
        // histories join under the newest replacement still captured now. No other layers are ever joined.
        var replaced = Replacements(sessions);
        var successors = replaced.ToLookup(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        string Canonical(string uuid) => CanonicalUuid(uuid, replaced);
        var currentLayers = current.Packets.Select(p => p.Layer).Distinct().ToArray();
        var currentUuids = currentLayers.Select(l => l.Uuid).ToHashSet(StringComparer.Ordinal);
        var currentRoutes = currentLayers.Where(layer => !successors[layer.Uuid].Any(currentUuids.Contains))
            .GroupBy(layer => Canonical(layer.Uuid), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var sources = new Dictionary<LayerKey, List<SourceLayer>>();
        var globalIssues = new List<PlanIssue>();
        for (int order = 0; order < sessions.Length; order++)
        {
            var session = sessions[order];
            if (session.Frequency <= 0 || session.Replay.SessionId != session.SessionId || session.Replay.Frequency != session.Frequency)
                throw new InvalidDataException("工程会话的原生时钟或重放身份不一致：" + session.SessionId);
            if (session.Packets.Any(p => p.Layer.SessionId != session.SessionId))
                throw new InvalidDataException("工程会话包含其他 session 的图层差异包。");
            bool recordedContact = session.Frames.Any(f => J.Text(f, "kind") == "penBegin" &&
                J.U64(f, "appendId") <= session.BoundaryAppendId);
            if (session.Packets.Count == 0)
            {
                if (recordedContact) globalIssues.Add(new(PlanIssueCode.ChainBroken,
                    "历史会话记录了笔接触但没有可验证的整层捕获：" + session.SessionId));
                continue;
            }
            EditIndex? cached = null;
            bool reused = order < prior.Count && sourceIndexes is not null && sourceIndexes.TryGetValue(session.SessionId, out cached);
            var native = reused ? cached! : EditIndexBuilder.Build(session);
            foreach (var layer in native.Layers.Values)
            {
                LayerKey route = layer.Layer;
                var routeIssues = new List<PlanIssue>();
                if (currentRoutes.TryGetValue(Canonical(layer.Layer.Uuid), out var matches) && matches.Length == 1)
                    route = matches[0];
                else routeIssues.Add(new(PlanIssueCode.LayerIdentityUnconfirmed,
                    "历史图层尚未唯一对应当前 CSP 捕获：" + layer.Layer.Uuid));
                var packets = layer.Packets.OrderBy(p => p.TriggerTicks).ToArray();
                var (baseline, final) = Endpoints(layer);
                var issues = layer.Issues.ToList();
                // A full, unchanged capture with no contacts is still a continuity checkpoint.
                if (layer.Strokes.Count == 0 && packets.All(p => p.IsRecordingBaseline) && SamePixels(baseline, final))
                    issues.RemoveAll(i => i.Code == PlanIssueCode.NoBaseline && i.Message == "图层没有可重建的差异链。");
                issues.AddRange(routeIssues);
                // Baseline and final come from the layer alone, so a reused history layer is verified once.
                issues.AddRange(reused ? VerifiedHistory(layer) : VerifyPixels(layer, baseline, final, session.SessionId));
                if (!sources.TryGetValue(route, out var chain)) sources.Add(route, chain = []);
                chain.Add(new(order, session, layer, baseline, final, issues));
            }
        }
        var layers = new Dictionary<LayerKey, LayerIndex>();
        var strokes = new Dictionary<EditableStrokeKey, EditableStroke>();
        var images = new Dictionary<DirtyImageKey, DirtyImage>();
        foreach (var (route, unordered) in sources)
        {
            // One session can hold an original and its replacement: order by session, then by capture time.
            var chain = unordered.OrderBy(source => source.Order)
                .ThenBy(source => source.Index.Packets.Count == 0 ? long.MinValue : source.Index.Packets.Min(p => p.TriggerTicks)).ToList();
            var issues = new List<PlanIssue>();
            var packets = new List<LayerPacket>();
            var layerStrokes = new List<EditableStroke>();
            var commands = new List<UndoRedoEvidence>();
            var undoCheckpoints = new List<UndoCheckpoint>();
            var bridges = new Dictionary<int, LayerPacket>();
            for (int i = 1; i < chain.Count; i++)
                if (!SamePixels(chain[i - 1].Current, chain[i].Baseline) &&
                    TryBridge(chain[i - 1], chain[i], route, out var bridge)) bridges.Add(i, bridge!);
            for (int i = 0; i < chain.Count; i++)
            {
                var source = chain[i];
                bool joinedToNext = i + 1 < chain.Count &&
                    (SamePixels(source.Current, chain[i + 1].Baseline) || bridges.ContainsKey(i + 1));
                issues.AddRange(source.Issues.Where(issue => !(joinedToNext && (source.Session.SessionId != current.SessionId || source.Index.Layer != route) &&
                    issue.Code == PlanIssueCode.CaptureMismatch && issue.Message == "编辑边界缺少最后一组的整层捕获。"))
                    .Select(issue => MapIssue(issue, route, source.Session.SessionId)));
                if (i > 0 && !SamePixels(chain[i - 1].Current, source.Baseline) && !bridges.ContainsKey(i))
                    issues.Add(new(PlanIssueCode.ChainBroken,
                        $"图层 {route.Uuid} 的会话衔接像素不连续或捕获缺失：{chain[i - 1].Session.SessionId} → {source.Session.SessionId}。"));
                if (bridges.TryGetValue(i, out var observedBridge))
                {
                    packets.Add(observedBridge);
                    foreach (var image in observedBridge.Images) images.Add(image.Key, image);
                }
                foreach (var packet in source.Index.Packets)
                {
                    var mappedImages = packet.Images.Select(image => image with
                    { Key = MapImageKey(image.Key, route, source.Session.SessionId) }).ToArray();
                    foreach (var image in mappedImages) images.Add(image.Key, image);
                    packets.Add(packet with { PacketId = Scoped(source.Session.SessionId, packet.PacketId), Layer = route,
                        SessionOrder = source.Order, SourceLayer = packet.Layer, Images = mappedImages,
                        AfterSnapshotId = packet.AfterSnapshotId is { } after ? Scoped(source.Session.SessionId, after) : null,
                        NowSnapshotId = packet.NowSnapshotId is { } now ? Scoped(source.Session.SessionId, now) : null });
                }
                foreach (var stroke in source.Index.Strokes)
                {
                    var mapped = stroke with { Layer = route, SessionOrder = source.Order, SourceLayer = stroke.Layer,
                        Images = stroke.Images.Select(key => MapImageKey(key, route, source.Session.SessionId)).ToArray() };
                    strokes.Add(mapped.Key, mapped); layerStrokes.Add(mapped);
                }
                commands.AddRange(source.Index.UndoRedo.Select(command => command with { SessionOrder = source.Order }));
                undoCheckpoints.AddRange(source.Index.UndoCheckpoints.Select(checkpoint => checkpoint with
                { PacketId = Scoped(source.Session.SessionId, checkpoint.PacketId) }));
            }
            var last = chain[^1];
            if (last.Session.SessionId != current.SessionId)
                issues.Add(new(PlanIssueCode.CaptureMismatch, "当前编辑边界没有捕获历史图层：" + route.Uuid));
            var orderedPackets = packets.OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
            var orderedStrokes = layerStrokes.OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray();
            layers.Add(route, new(route, orderedPackets, orderedStrokes, issues.Count == 0, issues.ToArray(),
                chain[0].Baseline, last.Current)
            { UndoRedo = commands.OrderBy(c => c.SessionOrder).ThenBy(c => c.InputAppendId).ToArray(),
                UndoCheckpoints = undoCheckpoints });
        }
        return new(layers, strokes, images, globalIssues.Concat(layers.Values.SelectMany(layer => layer.Issues)).ToArray());
    }

    /// <summary>reproduction UUID → the layer it replaced, from every completed Memoline editing action.</summary>
    internal static IReadOnlyDictionary<string, string> Replacements(IEnumerable<SessionSnapshot> sessions)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var session in sessions)
            foreach (var frame in session.Frames.Where(f => J.Text(f, "kind") == "editingActionCompleted" &&
                         J.U64(f, "appendId") <= session.BoundaryAppendId).OrderBy(f => J.U64(f, "appendId")))
                foreach (var item in J.Array(J.Get(J.Get(frame, "data"), "layerReplacements")))
                    if (J.Text(item, "replaced") is { Length: > 0 } old && J.Text(item, "reproduction") is { Length: > 0 } now && old != now)
                        map[now] = old;
        return map;
    }

    internal static bool HasReplacements(SessionSnapshot session) => Replacements([session]).Count > 0;

    internal static string CanonicalUuid(string uuid, IReadOnlyDictionary<string, string> replaced)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(uuid) && replaced.TryGetValue(uuid, out var earlier)) uuid = earlier;
        return uuid;
    }

    private static (ImageRgba? Baseline, ImageRgba? Final) Endpoints(LayerIndex layer)
    {
        var packets = layer.Packets.OrderBy(p => p.TriggerTicks).ToArray();
        return (packets.Select(p => p.Before).FirstOrDefault(i => i is not null) ??
                packets.Where(p => p.IsRecordingBaseline).Select(p => p.Current).FirstOrDefault(i => i is not null) ?? layer.Baseline,
            packets.Select(p => p.Current).LastOrDefault(i => i is not null) ?? layer.Current);
    }

    // Prior sessions' indexes are immutable and shared by every boundary of a recording.
    private static readonly ConditionalWeakTable<LayerIndex, IReadOnlyList<PlanIssue>> Verified = new();
    private static IReadOnlyList<PlanIssue> VerifiedHistory(LayerIndex layer) => Verified.GetValue(layer, _ =>
    {
        var (baseline, final) = Endpoints(layer);
        return VerifyPixels(layer, baseline, final, layer.Layer.SessionId);
    });

    /// <summary>Verifies the pixel chains of history indexes ahead of the first boundary that joins them.</summary>
    internal static void VerifyHistory(IEnumerable<EditIndex> indexes)
    {
        foreach (var layer in indexes.SelectMany(index => index.Layers.Values)) VerifiedHistory(layer);
    }

    /// <summary>
    /// Indexes and verifies sealed sessions for <see cref="Build"/>. Each session is decoded only while it is
    /// indexed and verified, and kept compressed afterwards.
    /// </summary>
    internal static IReadOnlyDictionary<string, EditIndex> BuildHistory(IEnumerable<SessionSnapshot> sessions)
    {
        var indexes = new Dictionary<string, EditIndex>();
        foreach (var session in sessions)
        {
            var index = EditIndexBuilder.Build(session);
            VerifyHistory([index]);
            EditingMemory.Park(session); EditingMemory.Park(index);
            indexes.Add(session.SessionId, index);
            // Verification copies whole canvases; collect them before the next session allocates its own.
            EditingMemory.Collect();
        }
        EditingMemory.Compact();
        return indexes;
    }

    private static IReadOnlyList<PlanIssue> VerifyPixels(LayerIndex layer, ImageRgba? baseline, ImageRgba? final, string session)
    {
        if (baseline is null || final is null)
            return [new(PlanIssueCode.CaptureMismatch, "会话缺少完整 baseline 或末层捕获，无法连接历史：" + session)];
        try
        {
            baseline.Validate(); final.Validate();
            if (baseline.Width != final.Width || baseline.Height != final.Height)
                return [new(PlanIssueCode.ChainBroken, "会话前后画布尺寸不同：" + session)];
            var cursor = baseline.Copy();
            foreach (var packet in layer.Packets.Where(p => !p.IsRecordingBaseline).OrderBy(p => p.TriggerTicks))
            {
                ImageCompositor.ValidateGroup(packet.Images, cursor.Width, cursor.Height);
                foreach (var image in packet.Images)
                    foreach (var (x, y) in image.Mask.Pixels())
                    {
                        int source = ((y - image.Mask.Top) * image.Mask.Width + x - image.Mask.Left) * 4;
                        int target = (y * cursor.Width + x) * 4;
                        if (!cursor.Bytes.AsSpan(target, 4).SequenceEqual(image.After.Bytes.AsSpan(source, 4)))
                            return [new(PlanIssueCode.ChainBroken, "差异补丁的前像素与会话 baseline 链不一致：" + session + "/" + packet.PacketId)];
                    }
                foreach (var image in packet.Images) ImageCompositor.Replace(cursor, image);
            }
            return SamePixels(cursor, final) ? [] : [new(PlanIssueCode.ChainBroken,
                "会话差异链重建结果与末层捕获不一致：" + session)];
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        { return [new(PlanIssueCode.ChainBroken, "会话像素链无效：" + session + "：" + ex.Message)]; }
    }

    // Endpoint pixels preserve the timeline, but cannot identify missing operations or
    // their dependencies. Any surviving earlier difference must pass the unknown-interval gate.
    private static bool TryBridge(SourceLayer previous, SourceLayer next, LayerKey route, out LayerPacket? bridge)
    {
        bridge = null;
        if (previous.Current is not { } before || next.Baseline is not { } now ||
            !CompletePixels(before) || !CompletePixels(now) || before.Width != now.Width || before.Height != now.Height)
            return false;
        long earliest = next.Index.Packets.Min(p => p.TriggerTicks);
        if (earliest == long.MinValue) return false;
        var oldPixels = MemoryMarshal.Cast<byte, uint>(before.Bytes.AsSpan());
        var newPixels = MemoryMarshal.Cast<byte, uint>(now.Bytes.AsSpan());
        int left = before.Width, top = before.Height, right = -1, bottom = -1;
        for (int i = 0; i < oldPixels.Length; i++)
        {
            if (oldPixels[i] == newPixels[i]) continue;
            int x = i % before.Width, y = i / before.Width;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        if (right < left) return false;
        int width = right - left + 1, height = bottom - top + 1;
        var mask = new PixelMask(left, top, width, height);
        var oldCrop = new byte[checked(width * height * 4)]; var newCrop = new byte[oldCrop.Length];
        for (int y = top; y <= bottom; y++)
        {
            int offset = (y * before.Width + left) * 4, target = (y - top) * width * 4;
            before.Bytes.AsSpan(offset, width * 4).CopyTo(oldCrop.AsSpan(target));
            now.Bytes.AsSpan(offset, width * 4).CopyTo(newCrop.AsSpan(target));
            for (int x = left; x <= right; x++)
            {
                int index = y * before.Width + x;
                if (oldPixels[index] != newPixels[index]) mask.Set(x, y);
            }
        }
        // A replacement inside one session joins two layers of that session; the layer keeps the bridge unique.
        string id = Scoped(next.Session.SessionId, "project-observed-gap/" + previous.Session.SessionId +
            (previous.Index.Layer.Uuid == next.Index.Layer.Uuid ? "" : "/" + previous.Index.Layer.Uuid));
        var image = new DirtyImage(new(route, id, "unrecorded-change"), mask,
            new(width, height, oldCrop), new(width, height, newCrop), [], IsCausal: true, HasUnreplayableEvents: true)
        { UnknownHistoryInterval = true,
            W = new(mask.Left, mask.Top, mask.Width, mask.Height), Probe = new(mask.Left, mask.Top, mask.Width, mask.Height), Impact = mask.Clone() };
        bridge = new(id, route, earliest - 1, before, now, [image],
            AfterSnapshotId: Scoped(previous.Session.SessionId, "last-observed-pixels"),
            NowSnapshotId: Scoped(next.Session.SessionId, "first-observed-pixels"), BaselineSource: "observedProjectContinuation",
            CanvasWidth: before.Width, CanvasHeight: before.Height)
        { SessionOrder = next.Order, SourceLayer = next.Index.Layer };
        return true;
    }

    // A parked image restores exactly Width × Height × 4 bytes, so it is complete without being restored.
    private static bool CompletePixels(ImageRgba? image) => image is { Width: > 0, Height: > 0 } &&
        (long)image.Width * image.Height <= int.MaxValue / 4 &&
        (image.IsParked || image.Bytes is { } bytes && bytes.Length == (long)image.Width * image.Height * 4);
    private static bool SamePixels(ImageRgba? first, ImageRgba? second) => first is { } a && second is { } b &&
        CompletePixels(a) && CompletePixels(b) && a.SamePixels(b);
    private static string Scoped(string session, string value) => Uri.EscapeDataString(session) + "/" + Uri.EscapeDataString(value);
    private static DirtyImageKey MapImageKey(DirtyImageKey key, LayerKey route, string session) =>
        new(route, Scoped(session, key.PacketId), key.ImageId);
    private static PlanIssue MapIssue(PlanIssue issue, LayerKey route, string session) => issue with
    { Image = issue.Image is { } key ? MapImageKey(key, route, session) : null };
}

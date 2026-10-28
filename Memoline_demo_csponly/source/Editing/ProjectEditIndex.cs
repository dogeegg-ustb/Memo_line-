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
        // Each captured layer's history goes to the layer that continues it now (see LayerLineage); layers with no
        // successor in CSP were deleted by the user and keep their own route, marked deleted.
        var lineage = LayerLineage.Build(sessions);
        var currentLayers = current.Packets.Select(p => p.Layer).Distinct().ToArray();
        var sources = new Dictionary<string, List<SourceLayer>>(StringComparer.Ordinal);
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
                if (lineage.Undone(layer.Layer.Uuid)) continue;
                var packets = layer.Packets.OrderBy(p => p.TriggerTicks).ToArray();
                var (baseline, final) = Endpoints(layer);
                var issues = layer.Issues.ToList();
                // A full, unchanged capture with no contacts is still a continuity checkpoint.
                if (layer.Strokes.Count == 0 && packets.All(p => p.IsRecordingBaseline) && SamePixels(baseline, final))
                    issues.RemoveAll(i => i.Code == PlanIssueCode.NoBaseline && i.Message == "图层没有可重建的差异链。");
                // Baseline and final come from the layer alone, so a reused history layer is verified once.
                issues.AddRange(reused ? VerifiedHistory(layer) : VerifyPixels(layer, baseline, final, session.SessionId));
                string owner = lineage.Owner(layer.Layer.Uuid);
                if (!sources.TryGetValue(owner, out var chain)) sources.Add(owner, chain = []);
                chain.Add(new(order, session, layer, baseline, final, issues));
            }
        }
        var layers = new Dictionary<LayerKey, LayerIndex>();
        var strokes = new Dictionary<EditableStrokeKey, EditableStroke>();
        var images = new Dictionary<DirtyImageKey, DirtyImage>();
        var routes = new Dictionary<string, LayerKey>(StringComparer.Ordinal);
        foreach (var (owner, unordered) in sources)
        {
            // One session can hold an original and its replacement: order by session, then by capture time.
            var chain = unordered.OrderBy(source => source.Order)
                .ThenBy(source => source.Index.Packets.Count == 0 ? long.MinValue : source.Index.Packets.Min(p => p.TriggerTicks)).ToList();
            bool deleted = !lineage.Alive.Contains(owner);
            var issues = new List<PlanIssue>();
            var live = currentLayers.Where(l => l.Uuid == owner).ToArray();
            var route = !deleted && live.Length > 0 ? live[^1]
                : chain.LastOrDefault(source => source.Index.Layer.Uuid == owner)?.Index.Layer ?? chain[^1].Index.Layer with { Uuid = owner };
            if (live.Length > 1) issues.Add(new(PlanIssueCode.LayerIdentityUnconfirmed, "当前 CSP 捕获中同一图层 UUID 对应多个图层：" + owner));
            routes.Add(owner, route);
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
            if (!deleted && last.Session.SessionId != current.SessionId)
                issues.Add(new(PlanIssueCode.CaptureMismatch, "当前编辑边界没有捕获历史图层：" + route.Uuid));
            var orderedPackets = packets.OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
            var orderedStrokes = layerStrokes.OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray();
            layers.Add(route, new(route, orderedPackets, orderedStrokes, issues.Count == 0, issues.ToArray(),
                chain[0].Baseline, last.Current)
            { UndoRedo = commands.OrderBy(c => c.SessionOrder).ThenBy(c => c.InputAppendId).ToArray(),
                UndoCheckpoints = undoCheckpoints, Deleted = deleted });
        }
        foreach (var merge in lineage.Merges)
            if (routes.TryGetValue(lineage.Owner(merge.Predecessor), out var from) && routes.TryGetValue(lineage.Owner(merge.Successor), out var into) &&
                from != into && layers.TryGetValue(from, out var source) && layers.TryGetValue(into, out var target))
            {
                if (Fold(source, target, merge, strokes, images) is { } folded)
                {
                    layers[into] = folded; layers.Remove(from);
                    foreach (var owner in routes.Where(r => r.Value == from).Select(r => r.Key).ToArray()) routes[owner] = into;
                }
                else layers[from] = source with { Issues = [.. source.Issues, new(PlanIssueCode.ChainBroken,
                    $"合并进图层 {merge.Successor} 的历史无法与它的像素链衔接，按已删除图层显示。")] };
            }
        // Executed Memoline edits turn what they changed into new versions (see MemolineEdits).
        return MemolineEdits.Apply(new(layers, strokes, images,
            globalIssues.Concat(layers.Values.Where(layer => !layer.Deleted).SelectMany(layer => layer.Issues)).ToArray()),
            MemolineEdits.Read(sessions, lineage));
    }

    /// <summary>
    /// A merged layer's history continues inside the layer it went into. Its packets are moved to just before the
    /// target's first capture after the merge and expressed in the target's pixels: each of its images composited
    /// (normal, 100 %) over the target as it was then. A bridge brings its starting pixels in and another one takes
    /// its final pixels out again, so the target's own capture of the merge stays as recorded and the chain stays
    /// exact. Precise undo of a merged stroke then rewinds the target to the merged layer's pixels before that stroke.
    /// Null when either chain is unusable or the moved packets do not fit between the target's captures.
    /// </summary>
    private static LayerIndex? Fold(LayerIndex source, LayerIndex target, LayerLineage.Link merge,
        Dictionary<EditableStrokeKey, EditableStroke> strokes, Dictionary<DirtyImageKey, DirtyImage> images)
    {
        if (!source.ChainValid || source.Current is null || target.Current is null) return null;
        var route = target.Layer;
        var targetPackets = target.Packets.Where(p => !p.IsRecordingBaseline).OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
        var moved = source.Packets.Where(p => !p.IsRecordingBaseline).OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
        var next = targetPackets.FirstOrDefault(p => (p.SessionOrder, p.TriggerTicks).CompareTo((merge.Order, merge.Ticks)) >= 0);
        var previous = targetPackets.LastOrDefault(p => (p.SessionOrder, p.TriggerTicks).CompareTo((merge.Order, merge.Ticks)) < 0);
        var under = next is null ? target.Current.Copy() : new LayerTimeline(target.Packets, target.Current).BeforePacket(next.PacketId);
        var start = source.Baseline ?? new ImageRgba(under.Width, under.Height, new byte[under.Bytes.Length]);
        if (start.Width != under.Width || start.Height != under.Height || source.Current.Width != under.Width || source.Current.Height != under.Height)
            return null;
        int count = moved.Length + 2;
        long high = next is { } following && following.SessionOrder == merge.Order ? Math.Min(following.TriggerTicks - 1, merge.Ticks) : merge.Ticks;
        long low = previous is { } preceding && preceding.SessionOrder == merge.Order ? preceding.TriggerTicks : long.MinValue / 2;
        if (high - count <= low) return null;
        string prefix = "merged/" + Uri.EscapeDataString(source.Layer.Uuid) + "/";
        DirtyImageKey Key(DirtyImageKey key) => new(route, prefix + key.PacketId, key.ImageId);
        var added = new List<LayerPacket>();
        long tick = high - count;
        LayerPacket? Bridge(string id, ImageRgba before, ImageRgba now)
        {
            tick++;
            if (ObservedChange(new(route, prefix + id, "merge"), before, now) is not { } image) return null;
            return new(prefix + id, route, tick, null, null, [image], AfterSnapshotId: prefix + id + "/before",
                NowSnapshotId: prefix + id + "/now", BaselineSource: "mergedLayer", CanvasWidth: under.Width, CanvasHeight: under.Height)
            { SessionOrder = merge.Order, SourceLayer = source.Layer };
        }
        if (Bridge("in", under, Over(start, under)) is { } entering) added.Add(entering);
        foreach (var packet in moved)
        {
            tick++;
            var translated = packet.Images.Select(image => image with
            {
                Key = Key(image.Key),
                After = OverCrop(image.After, under, image.Mask), Now = OverCrop(image.Now, under, image.Mask),
            }).ToArray();
            added.Add(packet with { PacketId = prefix + packet.PacketId, Layer = route, TriggerTicks = tick, Before = null, Current = null,
                Images = translated, IsBoundary = false, SessionOrder = merge.Order, SourceLayer = packet.SourceLayer ?? packet.Layer,
                AfterSnapshotId = packet.AfterSnapshotId is { } a ? prefix + a : null, NowSnapshotId = packet.NowSnapshotId is { } b ? prefix + b : null });
        }
        if (Bridge("out", Over(source.Current, under), under) is { } leaving) added.Add(leaving);
        foreach (var image in source.Packets.SelectMany(p => p.Images)) images.Remove(image.Key);
        foreach (var image in added.SelectMany(p => p.Images)) images.Add(image.Key, image);
        var mergedStrokes = new List<EditableStroke>();
        foreach (var stroke in source.Strokes)
        {
            var mapped = stroke with { Layer = route, Images = stroke.Images.Select(Key).ToArray(), MergedFrom = stroke.MergedFrom ?? source.Layer.Uuid };
            strokes[stroke.Key] = mapped; mergedStrokes.Add(mapped);
        }
        return target with
        {
            Packets = target.Packets.Concat(added).OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray(),
            Strokes = target.Strokes.Concat(mergedStrokes).OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray(),
            UndoRedo = target.UndoRedo.Concat(source.UndoRedo).OrderBy(c => c.SessionOrder).ThenBy(c => c.InputAppendId).ToArray(),
            UndoCheckpoints = target.UndoCheckpoints.Concat(source.UndoCheckpoints.Select(c => c with { PacketId = prefix + c.PacketId })).ToArray(),
        };
    }

    /// <summary>Straight-alpha source-over of two full canvases.</summary>
    private static ImageRgba Over(ImageRgba top, ImageRgba bottom)
    {
        var result = bottom.Copy();
        var a = top.Bytes; var b = result.Bytes;
        for (int i = 0; i < b.Length; i += 4) Over(a.AsSpan(i, 4), b.AsSpan(i, 4));
        return result;
    }

    /// <summary><paramref name="top"/> is a crop at <paramref name="mask"/>'s bounds; the result is that crop over the canvas.</summary>
    private static ImageRgba OverCrop(ImageRgba top, ImageRgba canvas, PixelMask mask)
    {
        var result = new byte[top.Bytes.Length];
        var source = top.Bytes;
        for (int y = 0; y < mask.Height; y++)
        {
            int row = ((mask.Top + y) * canvas.Width + mask.Left) * 4;
            canvas.Bytes.AsSpan(row, mask.Width * 4).CopyTo(result.AsSpan(y * mask.Width * 4));
            for (int x = 0; x < mask.Width; x++) Over(source.AsSpan((y * mask.Width + x) * 4, 4), result.AsSpan((y * mask.Width + x) * 4, 4));
        }
        return new(top.Width, top.Height, result);
    }

    private static void Over(ReadOnlySpan<byte> top, Span<byte> bottom)
    {
        int sa = top[3];
        if (sa == 0) return;
        if (sa == 255) { top.CopyTo(bottom); return; }
        int da = bottom[3];
        // In 1/255² units: alpha = a·255 + b·(255 − a); colour = (top·a·255 + bottom·b·(255 − a)) / alpha.
        int alpha = sa * 255 + da * (255 - sa);
        for (int c = 0; c < 3; c++)
            bottom[c] = (byte)(((long)top[c] * sa * 255 + (long)bottom[c] * da * (255 - sa) + alpha / 2) / alpha);
        bottom[3] = (byte)((alpha + 127) / 255);
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
        // A replacement inside one session joins two layers of that session; the layer keeps the bridge unique.
        string id = Scoped(next.Session.SessionId, "project-observed-gap/" + previous.Session.SessionId +
            (previous.Index.Layer.Uuid == next.Index.Layer.Uuid ? "" : "/" + previous.Index.Layer.Uuid));
        if (ObservedChange(new(route, id, "unrecorded-change"), before, now) is not { } image) return false;
        bridge = new(id, route, earliest - 1, before, now, [image],
            AfterSnapshotId: Scoped(previous.Session.SessionId, "last-observed-pixels"),
            NowSnapshotId: Scoped(next.Session.SessionId, "first-observed-pixels"), BaselineSource: "observedProjectContinuation",
            CanvasWidth: before.Width, CanvasHeight: before.Height)
        { SessionOrder = next.Order, SourceLayer = next.Index.Layer };
        return true;
    }

    /// <summary>The pixels that differ between two full canvases as one unrecorded change; null when none do.</summary>
    private static DirtyImage? ObservedChange(DirtyImageKey key, ImageRgba before, ImageRgba now)
    {
        var oldPixels = MemoryMarshal.Cast<byte, uint>(before.Bytes.AsSpan());
        var newPixels = MemoryMarshal.Cast<byte, uint>(now.Bytes.AsSpan());
        int left = before.Width, top = before.Height, right = -1, bottom = -1;
        for (int i = 0; i < oldPixels.Length; i++)
        {
            if (oldPixels[i] == newPixels[i]) continue;
            int x = i % before.Width, y = i / before.Width;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        if (right < left) return null;
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
        return new DirtyImage(key, mask, new(width, height, oldCrop), new(width, height, newCrop), [], IsCausal: true, HasUnreplayableEvents: true)
        { UnknownHistoryInterval = true,
            W = new(mask.Left, mask.Top, mask.Width, mask.Height), Probe = new(mask.Left, mask.Top, mask.Width, mask.Height), Impact = mask.Clone() };
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

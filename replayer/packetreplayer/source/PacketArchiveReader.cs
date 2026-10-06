using System.IO.Compression;
using System.Text.Json;
using MemolineDemo;
using StrokeReplay;

namespace PacketReplay;

internal sealed record PacketInfo(int Number, string Id, long FromTicks, long ToTicks, string Status, IReadOnlySet<ulong> EventAppends, JsonElement Json);
internal sealed record PacketDocument(string Path, BundleVerificationResult Verification, IReadOnlyList<JsonElement> Frames,
    MemolineReplayDocument Mechanical, IReadOnlyList<PacketInfo> Packets, IReadOnlyList<HistorySlot> History);
internal sealed record PacketStroke(MemolineReplayStroke Stroke, HistoricalState State, bool Clipped);
internal sealed record PacketStep(ulong Anchor, ulong Order, string Kind, PacketStroke? Drawing = null,
    BrushSnapshot? Brush = null, string? Layer = null, CanvasViewSnapshot? View = null, string? Command = null, string? Error = null,
    JsonElement Color = default)
{
    internal string Description => Kind switch
    {
        "stroke" => $"笔画 · 操作 {Drawing!.Stroke.OperationId} · {Drawing.Stroke.Samples.Count} 点" + (Drawing.Clipped ? " · 跨包片段" : ""),
        "brush" => $"笔刷 · {Brush?.Name ?? "未确认"} · {Brush?.Numbers.Count ?? 0} 项数字属性",
        "layer" => $"图层 · {Layer ?? "未确认"}",
        "color" => $"颜色 · {J.Text(Color, "kind") ?? "未确认"} · {J.Text(Color, "hex") ?? string.Join(",", J.Array(J.Get(Color, "rgb")))}",
        "view" => $"视图 · {View?.ScalePercent:0.###}% / {View?.RotationDegrees:0.###}°",
        "command" => $"命令 · {Command}",
        _ => Error ?? Kind
    };
}
internal sealed record PacketPlan(PacketInfo Packet, IReadOnlyList<PacketStroke> Strokes, IReadOnlyList<PacketStep> Steps, IReadOnlyList<string> Notes,
    HistoricalState InitialState)
{
    internal HistoricalState StateBefore(int stepIndex) => Steps.Take(stepIndex).Aggregate(InitialState, (state, step) => state.Apply(step));
}

internal static class PacketArchiveReader
{
    internal static PacketDocument Read(string path, CancellationToken token = default)
    {
        path = System.IO.Path.GetFullPath(path);
        // Use the same authoritative container/hash/pointer validator as Memoline.
        var verification = BundleArchive.Verify(path, token);
        var frames = BundleArchive.ReadMechanical(path).Where(f => J.Text(f, "kind") != "screenshotBlob" && J.Text(f, "kind") != "mouseCursorChanged").ToArray();
        token.ThrowIfCancellationRequested();
        var mechanical = MemolineReplayReader.Parse(path, frames, allowEmpty: true);
        using var zip = ZipFile.OpenRead(path);
        using var indexStream = zip.GetEntry("index.json")!.Open();
        using var index = JsonDocument.Parse(indexStream);
        var meta = index.RootElement.GetProperty("aggregation");
        var codec = J.Text(meta, "codec");
        if (codec is not (null or "brotli")) throw new InvalidDataException("不支持的聚集流编码。");
        using var encoded = zip.GetEntry(J.Text(meta, "entry") + (codec == "brotli" ? ".br" : ""))!.Open();
        using var decoded = codec == "brotli" ? new BrotliStream(encoded, CompressionMode.Decompress) : (Stream)encoded;
        using var reader = new StreamReader(decoded);
        var packets = new List<PacketInfo>();
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            using var row = JsonDocument.Parse(line);
            var p = row.RootElement;
            if (J.Text(p, "kind") != "packet") continue;
            packets.Add(new(0, J.Text(p, "id") ?? "packet", J.I64(p, "fromTicks"), J.I64(p, "toTicks"), J.Text(p, "status") ?? "unknown",
                J.Array(J.Get(p, "eventPointers")).Where(r => J.Get(r, "context").ValueKind != JsonValueKind.True).Select(r => J.U64(r, "appendId")).ToHashSet(), p.Clone()));
        }
        return new(path, verification, frames, mechanical, packets.OrderBy(p => p.FromTicks).ThenBy(p => p.ToTicks)
            .Select((p, i) => p with { Number = i + 1 }).ToArray(), StateHistory.Resolve(frames));
    }

    internal static PacketPlan Plan(PacketDocument document, PacketInfo packet)
    {
        var strokes = new List<PacketStroke>();
        var notes = new List<string>();
        foreach (var s in document.Mechanical.Strokes)
        {
            var selected = s.Samples.Where(p => packet.EventAppends.Contains(p.AppendId)).ToList();
            if (!selected.Any(p => p.InContact)) continue;
            bool clipped = selected[0].Ticks > s.StartTicks || s.EndTicks > packet.ToTicks
                || s.Samples.Any(p => p.AppendId > 0 && !packet.EventAppends.Contains(p.AppendId));
            if (selected[^1].InContact)
                selected.Add(selected[^1] with { Ticks = Math.Min(s.EndTicks, packet.ToTicks), Pressure = 0, InContact = false, AppendId = 0, EventId = 0 });
            var state = StateHistory.At(document.History, selected[0].EventId);
            strokes.Add(new(s with { StartTicks = selected[0].Ticks, EndTicks = selected[^1].Ticks, Samples = selected }, state, clipped));
        }
        if (strokes.Any(s => s.Clipped)) notes.Add("跨包笔画仅回放本包非 context 指针对应的片段，并在片段末尾抬笔。");
        if (strokes.Any(s => s.State.Brush is null || s.State.Layer is null)) notes.Add("部分笔画缺少已确认的历史笔刷或图层；这些笔画会阻止注入。");
        if (strokes.Any(s => s.State.Brush?.IgnoredProperties > 0)) notes.Add("文字、图标、复选框及未确认的笔刷属性不修改（包括弱／中／强）。");
        var steps = strokes.Select(s => new PacketStep(s.Stroke.Samples[0].EventId, 0, "stroke", Drawing: s)).ToList();
        var hardware = document.Frames.Where(f => J.Text(f, "path") == "hardware" && packet.EventAppends.Contains(J.U64(f, "appendId"))).ToArray();
        var anchors = hardware.Select(f => J.U64(f, "eventId")).ToHashSet();
        foreach (var slot in document.History.Where(s => anchors.Contains(s.Anchor)))
        {
            bool valid = J.Confirmed(slot.Data) && J.Get(J.Get(slot.Data, "evidence"), "causalAmbiguous").ValueKind != JsonValueKind.True;
            if (!valid && slot.Module is "brushState" or "currentLayerState" or "canvasViewState" or "colorState")
            {
                string reason = $"{slot.Module} 的历史状态未确认，跳过该状态事件；笔画仍须有已确认的前置状态。";
                steps.Add(new(slot.Anchor, slot.Order, "unconfirmed", Error: reason));
                notes.Add(reason);
                continue;
            }
            if (slot.Module == "brushState") steps.Add(new(slot.Anchor, slot.Order, "brush", Brush: valid ? BrushSnapshot.Parse(slot.Data) : null));
            else if (slot.Module == "currentLayerState") steps.Add(new(slot.Anchor, slot.Order, "layer",
                Layer: valid && J.Get(slot.Data, "state").ValueKind == JsonValueKind.String ? J.Get(slot.Data, "state").GetString() : null));
            else if (slot.Module == "colorState") steps.Add(new(slot.Anchor, slot.Order, "color", Color: J.Get(slot.Data, "state")));
            else if (slot.Module == "canvasViewState")
            {
                // View actions only need semantic geometry; drawing views retain viewport and dimensions from StrokeReplay.
                var view = valid ? CanvasViewSnapshot.ParseState(J.Get(slot.Data, "state")) : null;
                steps.Add(new(slot.Anchor, slot.Order, "view", View: view));
            }
        }
        foreach (var frame in document.Frames.Where(f => J.Text(f, "kind") == "shortcutResolved"))
        {
            var refs = J.Array(J.Get(frame, "relatedEventIds")).Select(r => r.TryUnsigned()).Where(anchors.Contains).ToArray();
            if (refs.Length == 0) continue;
            var commands = J.Array(J.Get(J.Get(frame, "data"), "matches")).Select(b => J.Text(b, "command")).Where(c => c is not null).Distinct().ToArray();
            foreach (var command in commands)
            {
                if (command is "undo" or "redo") steps.Add(new(refs[0], 0, "command", Command: command));
                else if (command!.StartsWith("view") || command.StartsWith("tool") || command.StartsWith("subtool")
                    || command.StartsWith("layerselect") || command is "applicationchangecurrentcolor" or "applicationchangecolortransparent") { }
                else steps.Add(new(refs[0], 0, "unsupported", Error: $"当前版本未支持包内命令 {command}，无法完整注入此包。"));
            }
        }
        notes.Add("画布外输入和导航由对应状态恢复代替；撤销／重做使用当前配置快捷键。颜色需与记录一致。");
        if (steps.Any(s => s.Kind == "unsupported")) notes.AddRange(steps.Where(s => s.Kind == "unsupported").Select(s => s.Error!));
        var firstEvent = hardware.Select(f => J.U64(f, "eventId")).Where(id => id > 0)
            .Concat(strokes.Select(s => s.Stroke.Samples[0].EventId)).DefaultIfEmpty(ulong.MaxValue).Min();
        var initial = StateHistory.At(document.History, firstEvent);
        return new(packet, strokes, steps.OrderBy(s => s.Anchor).ThenBy(s => s.Kind == "stroke" ? 0 : 1).ThenBy(s => s.Order).ToArray(), notes.Distinct().ToArray(), initial);
    }
}

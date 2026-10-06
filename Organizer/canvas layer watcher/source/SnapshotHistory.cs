using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using DirtyMatrix.Core;

namespace CanvasLayerWatcher;

internal sealed record LayerSnapshot(string Id, string RunId, string SessionId, long Generation, long SavedTicks, string ImageFile,
    int Width, int Height, LayerReference Layer, JsonElement RasterMetadata, RecognizerState[] RecognizerStates, long TriggerTicks = -1);
internal sealed record SnapshotPair(LayerSnapshot? After, LayerSnapshot? Now);
internal sealed record SnapshotHistoryIndex(LayerSnapshot? After, LayerSnapshot? Now,
    Dictionary<long, SnapshotPair>? LayerStacks = null);
internal sealed record SnapshotUpdate(string ImagePath, string PacketDirectory, bool Baseline, int DiffImages, int DirtyLabels);

internal sealed class SnapshotHistory
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public string Root { get; }
    public SnapshotPair Pair { get; private set; } = new(null, null);
    private Dictionary<long, SnapshotPair> _layerStacks = [];
    public IReadOnlyDictionary<long, SnapshotPair> LayerStacks => _layerStacks;
    public long EvidenceRetentionTicks => _layerStacks.Values.Select(pair => (pair.Now ?? pair.After)?.TriggerTicks)
        .OfType<long>().Where(ticks => ticks >= 0).DefaultIfEmpty(0).Min();
    private readonly string _run = Guid.NewGuid().ToString("N");
    public SnapshotHistory(string root)
    {
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Path.Combine(Root, "snapshots"));
        try
        {
            var index = JsonSerializer.Deserialize<SnapshotHistoryIndex>(File.ReadAllText(Path.Combine(Root, "current.json")), Json);
            if (index is not null)
            {
                Pair = new(index.After, index.Now);
                _layerStacks = index.LayerStacks ?? [];
                if ((Pair.Now ?? Pair.After) is { } active) _layerStacks.TryAdd(active.Layer.Id, Pair);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private static bool SameLayer(LayerReference a, LayerReference b)
        => a.Uuid is { Length: > 0 } && b.Uuid is { Length: > 0 } ? a.Uuid.Equals(b.Uuid, StringComparison.OrdinalIgnoreCase)
            : a.Id == b.Id;
    public SnapshotUpdate Advance(CaptureResult result, CaptureRequest request, string session,
        Func<long, long, EvidenceWindow> collect, Func<bool> valid, CancellationToken token)
    {
        using var parsed = JsonDocument.Parse(result.Metadata); var metadata = parsed.RootElement;
        int width = checked((int)J.Tick(metadata, "width", 0)), height = checked((int)J.Tick(metadata, "height", 0));
        if (width <= 0 || height <= 0 || (long)width * height > 64_000_000 || request.TriggerTicks < 0)
            throw new InvalidDataException("快照缺少有效的画布尺寸或 Recognizer triggerTicks");
        var layer = new LayerReference(J.Tick(metadata, "layerId", 0), result.LayerName, J.Text(metadata, "layerUuid"));
        if (layer.Id <= 0) throw new InvalidDataException("快照缺少有效的 CLIP 图层编号");
        // Reconnection, restart and resize invalidate the entire old epoch.
        // Switching layers only selects another independent two-slot stack.
        var nextStacks = _layerStacks.Where(item => (item.Value.Now ?? item.Value.After) is { } cached
            && cached.RunId == _run && cached.SessionId == session && cached.Generation == request.Generation
            && cached.Width == width && cached.Height == height && cached.TriggerTicks >= 0)
            .ToDictionary(item => item.Key, item => item.Value);
        var previous = nextStacks.GetValueOrDefault(layer.Id) is { } pair ? pair.Now ?? pair.After : null;
        bool baseline = previous is null || !SameLayer(previous.Layer, layer)
            || !File.Exists(Path.Combine(Root, previous.ImageFile));
        long from = baseline ? 0 : previous!.TriggerTicks;
        if (!baseline && request.TriggerTicks <= from) throw new InvalidDataException("新快照 triggerTicks 没有晚于前一张图像");
        var window = request.Context is { } context ? context.Evidence.Snapshot(from, request.TriggerTicks) : collect(from, request.TriggerTicks);
        if (window.SessionId != session) throw new InvalidOperationException("Recognizer 会话已改变，未提交快照");
        if (window.FromTicks != from || window.ToTicks != request.TriggerTicks) throw new InvalidOperationException("取证时间范围与 triggerTicks 不一致，未提交快照");
        var evidence = RecognitionEvidence.Build(window, request, new(width, height));
        string id = "trigger-" + request.TriggerTicks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var currentStates = window.States.Where(s => window.CaptureStateIds.Contains(s.Id)).ToArray();
        var snapshot = new LayerSnapshot(id, _run, session, request.Generation, result.SaveDispatchedTicks, "snapshots/" + id + ".png",
            width, height, layer, metadata.Clone(), currentStates, request.TriggerTicks);
        string stage = Path.Combine(Root, ".pending-" + id), packet = Path.Combine(Root, "packets", id);
        string imagePath = Path.Combine(Root, snapshot.ImageFile);
        string indexTemp = Path.Combine(Root, "current-" + id + ".tmp");
        bool movedImage = false, published = false, committed = false;
        Directory.CreateDirectory(stage);
        try
        {
            token.ThrowIfCancellationRequested();
            LayerDiffResult? diff = baseline ? null : LayerDiff.Compare(Path.Combine(Root, previous!.ImageFile), result.PngPath, evidence, stage, token);
            var labels = diff?.Labels ?? LayerDiff.Describe(evidence, new(width, height));
            // The locator owns a bounded thumbnail of the complete current
            // layer, so historical locations survive full snapshot rotation.
            const string canvasPreviewImage = "canvas-preview.png";
            CanvasThumbnail.Save(result.PngPath, Path.Combine(stage, canvasPreviewImage), new(width, height), token);
            // Historical packets own their cropped before/now pixels. Each layer
            // rotates its own full-size sources independently.
            object Descriptor(LayerSnapshot s) => new { s.Id, s.SessionId, s.Generation, s.TriggerTicks,
                saveDispatchedTicks = s.SavedTicks, s.SavedTicks, s.Width, s.Height, s.Layer, s.RasterMetadata, s.RecognizerStates };
            var manifest = new
            {
                schema = "dirty-matrix-image-diff/v1", kind = baseline ? "baseline" : "diff", id,
                after = Descriptor(baseline ? snapshot : previous!), now = baseline ? null : Descriptor(snapshot),
                recognizer = new { window.SessionId, window.Frequency, window.FromTicks, window.ToTicks, window.Complete,
                    window.States, window.CaptureStateIds, window.Inputs },
                capture = new { request.TriggerTicks, request.Ticks, request.TimeSource, request.TriggerKind, request.ViewPending,
                    layerName = layer.Name, layerId = layer.Id, layer,
                    observedLayerName = request.LayerName, initialLayerHint = request.Layer,
                    layerMapping = "currentLayerNameToSavedClipId", request.View,
                    request.Operations, saveDispatchedTicks = result.SaveDispatchedTicks, result.ControlResponse },
                search = new { tileSize = LayerDiff.TileSize, outsideDownsampleFactor = LayerDiff.CoarseFactor,
                    coarseWidth = diff?.CoarseWidth ?? (width + LayerDiff.CoarseFactor - 1) / LayerDiff.CoarseFactor,
                    coarseHeight = diff?.CoarseHeight ?? (height + LayerDiff.CoarseFactor - 1) / LayerDiff.CoarseFactor,
                    outsideInspection = "approximate; coarse hits refined at full resolution", evidence.Warnings,
                    fullResolutionComparedPixels = diff?.FullResolutionComparedPixels ?? 0,
                    coarseComparedPixels = diff?.CoarseComparedPixels ?? 0, changedPixels = diff?.ChangedPixels ?? 0 },
                predictedMatrix = diff?.PredictedMatrix ?? LayerDiff.Predicted(evidence, new(width, height)),
                labels, images = diff?.Images ?? [], canvasPreviewImage,
                imageConvention = "image is one original-RGBA now patch containing only pixels changed versus after, at full-canvas bounds; afterImage, maskImage and differenceImage are auxiliary; white mask pixels include erasure"
            };
            File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
            var next = baseline ? new SnapshotPair(snapshot, null) : new SnapshotPair(previous, snapshot);
            nextStacks[layer.Id] = next;
            File.WriteAllText(indexTemp, JsonSerializer.Serialize(new SnapshotHistoryIndex(next.After, next.Now, nextStacks), Json));
            token.ThrowIfCancellationRequested(); if (!valid()) throw new InvalidOperationException("会话或图层已改变，差异包未提交");
            File.Move(result.PngPath, imagePath); movedImage = true;
            Directory.CreateDirectory(Path.GetDirectoryName(packet)!); Directory.Move(stage, packet); published = true;
            File.Move(indexTemp, Path.Combine(Root, "current.json"), overwrite: true);
            var retained = nextStacks.Values.SelectMany(stack => new[] { stack.After, stack.Now }).OfType<LayerSnapshot>()
                .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var obsolete = _layerStacks.Values.SelectMany(stack => new[] { stack.After, stack.Now }).OfType<LayerSnapshot>()
                .Where(s => !retained.Contains(s.Id)).DistinctBy(s => s.Id).ToArray();
            _layerStacks = nextStacks; Pair = next; committed = true;
            foreach (var old in obsolete) SafeDeleteImage(old!.ImageFile);
            return new(imagePath, packet, baseline, diff?.Images.Length ?? 0, labels.Length);
        }
        finally
        {
            SaveCapture.TryDelete(indexTemp);
            if (!committed)
            {
                if (movedImage) SafeDeleteImage(snapshot.ImageFile);
                DeleteOwnDirectory(published ? packet : stage);
            }
        }
    }
    private void SafeDeleteImage(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(Root, relative));
        if (path.StartsWith(Path.Combine(Root, "snapshots") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) SaveCapture.TryDelete(path);
    }
    private void DeleteOwnDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("差异包清理路径越界");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Drawing.Imaging;
using DirtyMatrix.Core;

namespace CanvasLayerWatcher;

internal sealed record LayerSnapshot(string Id, string RunId, string SessionId, long Generation, long SavedTicks, string ImageFile,
    int Width, int Height, LayerReference Layer, JsonElement RasterMetadata, RecognizerState[] RecognizerStates, long TriggerTicks = -1,
    string? BaselineSource = null);
internal sealed record SnapshotPair(LayerSnapshot? After, LayerSnapshot? Now);
internal sealed record SnapshotHistoryIndex(LayerSnapshot? After, LayerSnapshot? Now,
    Dictionary<long, SnapshotPair>? LayerStacks = null, HashSet<string>? BaselineIds = null,
    Dictionary<string, LayerSnapshot>? Baselines = null);
internal sealed record SnapshotUpdate(string ImagePath, string PacketDirectory, bool Baseline, int DiffImages, int DirtyLabels);
internal sealed record BaselineSeed(LayerSnapshot Snapshot, string FullPngPath);
internal sealed record ChainVerification(bool Valid, string? Breakpoint, string? ExpectedAfterId, string? ActualAfterId, string? Message,
    string? Code = null);

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
    private HashSet<string> _baselineIds = new(StringComparer.Ordinal);
    private Dictionary<string, LayerSnapshot> _baselines = new(StringComparer.Ordinal);
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
                _baselineIds = index.BaselineIds ?? new(StringComparer.Ordinal);
                _baselines = index.Baselines ?? new(StringComparer.Ordinal);
                if ((Pair.Now ?? Pair.After) is { } active) _layerStacks.TryAdd(active.Layer.Id, Pair);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    /// <summary>recordingStart: the save that opens the recording; layerCreated: the first save that lists a
    /// layer created during it; reproduction: the layer an edit installed.</summary>
    internal static bool TrustedBaselineSource(string? source) => source is "recordingStart" or "createdEmpty" or "layerCreated" or "reproduction";

    /// <summary>The layer already has a snapshot (a baseline or a capture) in this session's history.</summary>
    public bool HasLayer(string uuid, string session, long generation)
        => _layerStacks.Values.SelectMany(pair => new[] { pair.After, pair.Now }).OfType<LayerSnapshot>()
            .Any(snapshot => snapshot.SessionId == session && snapshot.Generation == generation
                && string.Equals(snapshot.Layer.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

    private static bool SameLayer(LayerReference a, LayerReference b)
        => a.Uuid is { Length: > 0 } && b.Uuid is { Length: > 0 } ? a.Uuid.Equals(b.Uuid, StringComparison.OrdinalIgnoreCase)
            : a.Id == b.Id;

    public void SeedBaseline(LayerSnapshot snapshot, string fullPngPath)
    {
        SeedBaselines([new(snapshot, fullPngPath)]);
    }

    // Validate and stage every layer before changing the in-memory stacks or
    // current index. This keeps a retryable recording-start baseline atomic.
    public void SeedBaselines(IReadOnlyList<BaselineSeed> seeds)
    {
        if (seeds is null || seeds.Count == 0) throw new InvalidDataException("baseline 没有可提交的图层");
        var normalized = new List<(BaselineSeed Seed, LayerSnapshot Snapshot, string ImageFile, string Source)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var layerIds = new HashSet<long>();
        foreach (var seed in seeds)
        {
            var snapshot = seed.Snapshot;
            if (snapshot.Layer.Id <= 0 || snapshot.Width <= 0 || snapshot.Height <= 0 || snapshot.TriggerTicks < 0)
                throw new InvalidDataException("baseline 缺少有效的图层、尺寸或 triggerTicks");
            if (string.IsNullOrWhiteSpace(snapshot.Layer.Uuid) || string.IsNullOrWhiteSpace(snapshot.SessionId)
                || !TrustedBaselineSource(snapshot.BaselineSource))
                throw new InvalidDataException("baseline 缺少已确认的 UUID、会话或来源");
            if (!layerIds.Add(snapshot.Layer.Id)) throw new InvalidDataException("baseline 图层重复");
            string source = Path.GetFullPath(seed.FullPngPath);
            if (!File.Exists(source)) throw new FileNotFoundException("baseline PNG 不存在", source);
            string id = string.IsNullOrWhiteSpace(snapshot.Id) ? throw new InvalidDataException("baseline id 不能为空") : snapshot.Id;
            if (!string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal))
                throw new InvalidDataException("baseline id 不能包含路径分隔符");
            if (!ids.Add(id)) throw new InvalidDataException("baseline id 重复");
            string imageFile = "snapshots/" + id + ".png";
            if (File.Exists(Path.Combine(Root, imageFile))) throw new InvalidDataException("baseline id 已存在");
            var (width, height) = BaselineSize(source);
            if (width != snapshot.Width || height != snapshot.Height)
                throw new InvalidDataException("baseline PNG 尺寸与快照元数据不一致");
            normalized.Add((seed, snapshot with { RunId = _run, ImageFile = imageFile }, imageFile, source));
        }

        string stage = Path.Combine(Root, ".baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var written = new List<string>();
        try
        {
            // Stage all images as private temporary files first. A corrupt
            // source or disk failure therefore leaves the existing history intact.
            foreach (var item in normalized)
            {
                string staged = Path.Combine(stage, Path.GetFileName(item.ImageFile));
                // An 8-bit RGBA PNG whose chunks check out is kept byte for byte: decoding and re-encoding it with
                // GDI+ gave the same pixels and cost about a second per full canvas.
                if (FastPng.TryReadSize(File.ReadAllBytes(item.Source), out _, out _)) File.Copy(item.Source, staged);
                else { using var image = LayerDiff.Load(item.Source); image.Save(staged, ImageFormat.Png); }
            }
            var nextStacks = new Dictionary<long, SnapshotPair>(_layerStacks);
            var nextBaselineIds = new HashSet<string>(_baselineIds, StringComparer.Ordinal);
            var nextBaselines = new Dictionary<string, LayerSnapshot>(_baselines, StringComparer.Ordinal);
            SnapshotPair? last = null;
            foreach (var item in normalized)
            {
                string destination = Path.GetFullPath(Path.Combine(Root, item.ImageFile));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(Path.Combine(stage, Path.GetFileName(item.ImageFile)), destination);
                written.Add(destination);
                var next = new SnapshotPair(item.Snapshot, null);
                nextStacks[item.Snapshot.Layer.Id] = next;
                nextBaselineIds.Add(item.Snapshot.Id);
                nextBaselines.Add(item.Snapshot.Id, item.Snapshot);
                last = next;
            }
            var active = last ?? throw new InvalidDataException("baseline 没有可提交的图层");
            string temp = Path.Combine(Root, "current-baseline-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(new SnapshotHistoryIndex(active.After, active.Now, nextStacks, nextBaselineIds, nextBaselines), Json));
                File.Move(temp, Path.Combine(Root, "current.json"), overwrite: true);
            }
            finally { SaveCapture.TryDelete(temp); }
            _layerStacks = nextStacks;
            _baselineIds = nextBaselineIds;
            _baselines = nextBaselines;
            Pair = active;
        }
        catch
        {
            foreach (string path in written) SaveCapture.TryDelete(path);
            throw;
        }
        finally { DeleteOwnDirectory(stage); }
    }

    /// <summary>A baseline PNG's size: from its header when it is an 8-bit RGBA PNG with valid chunks, else decoded.</summary>
    private static (int Width, int Height) BaselineSize(string path)
    {
        byte[] png = File.ReadAllBytes(path);
        if (FastPng.TryReadSize(png, out int width, out int height))
        {
            if (!FastPng.ChunksValid(png)) throw new InvalidDataException("baseline PNG 数据块损坏");
            return (width, height);
        }
        using var image = LayerDiff.Load(path);
        return (image.Width, image.Height);
    }

    public void RegisterBaseline(LayerReference layer, string fullPngPath, long triggerTicks)
    {
        using var timing = StepTiming.Start("history", "registerBaseline");
        if (triggerTicks < 0) throw new InvalidDataException("baseline triggerTicks 必须非负");
        var image = BaselineSize(fullPngPath);
        var current = _layerStacks.GetValueOrDefault(layer.Id)?.Now ?? _layerStacks.GetValueOrDefault(layer.Id)?.After
            ?? Pair.Now ?? Pair.After;
        if (current is null || string.IsNullOrWhiteSpace(current.SessionId))
            throw new InvalidDataException("复现层 baseline 缺少当前录制会话");
        JsonElement metadata = JsonSerializer.SerializeToElement(new { width = image.Width, height = image.Height, fullCanvas = true, layerId = layer.Id, layerUuid = layer.Uuid });
        var snapshot = new LayerSnapshot("baseline-" + triggerTicks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + layer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"), _run,
            current.SessionId, current.Generation, triggerTicks, string.Empty, image.Width, image.Height, layer,
            metadata, current.RecognizerStates, triggerTicks, "reproduction");
        SeedBaseline(snapshot, fullPngPath);
    }

    /// <summary>The newest snapshot of a layer UUID in this session's stacks (a capture or a baseline), or null.</summary>
    public LayerSnapshot? Latest(string uuid, string session, long generation)
        => _layerStacks.Values.Select(pair => pair.Now ?? pair.After).OfType<LayerSnapshot>()
            .Where(s => s.SessionId == session && s.Generation == generation
                && string.Equals(s.Layer.Uuid, uuid, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(Root, s.ImageFile)))
            .MaxBy(s => s.TriggerTicks);

    /// <summary>
    /// The Memoline-operation packet: what an executed edit changed, from the replaced layer's last snapshot to its
    /// reproduction's installed baseline. It belongs to the reproduction and is the new version of the packets the
    /// edit rebuilt. It holds no recorded input (Memoline's own input never enters the native file), so its labels
    /// are the coarse ones; readers attach the edit's events from its native record. The stacks do not change.
    /// </summary>
    public SnapshotUpdate RecordOperation(LayerSnapshot replaced, LayerSnapshot reproduction, CaptureRequest request,
        long frequency, object operation, CancellationToken token)
    {
        if (replaced.Width != reproduction.Width || replaced.Height != reproduction.Height)
            throw new InvalidDataException("复现层与被替换图层的画布尺寸不同，不能生成记艺线操作包");
        if (replaced.SessionId != reproduction.SessionId || request.TriggerTicks < Math.Max(replaced.TriggerTicks, reproduction.TriggerTicks))
            throw new InvalidDataException("记艺线操作包的会话或 triggerTicks 与两张快照不一致");
        var size = new Size(reproduction.Width, reproduction.Height);
        var window = new EvidenceWindow(reproduction.SessionId, frequency, replaced.TriggerTicks, request.TriggerTicks, true, [], [], []);
        var evidence = RecognitionEvidence.Build(window, request, size);
        string id = "memoline-op-" + request.TriggerTicks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        string stage = Path.Combine(Root, ".pending-" + id), packet = Path.Combine(Root, "packets", id);
        string nowPath = Path.Combine(Root, reproduction.ImageFile);
        bool published = false;
        Directory.CreateDirectory(stage);
        try
        {
            using var timing = StepTiming.Start("history", "recordOperation");
            const string canvasPreviewImage = "canvas-preview.png";
            var thumbnail = Task.Run(() => CanvasThumbnail.Save(nowPath, Path.Combine(stage, canvasPreviewImage), size, token), token);
            LayerDiffResult diff;
            try { diff = LayerDiff.Compare(Path.Combine(Root, replaced.ImageFile), nowPath, evidence, stage, token); }
            catch { try { thumbnail.Wait(CancellationToken.None); } catch (Exception) { } throw; }
            thumbnail.GetAwaiter().GetResult();
            object Descriptor(LayerSnapshot s) => new { s.Id, s.SessionId, s.Generation, s.TriggerTicks, s.BaselineSource,
                saveDispatchedTicks = s.SavedTicks, s.SavedTicks, s.Width, s.Height, s.Layer, s.RasterMetadata, s.RecognizerStates };
            var manifest = new
            {
                schema = "dirty-matrix-image-diff/v1", comparison = "fullResolution/v2", kind = "diff", id,
                after = Descriptor(replaced), now = Descriptor(reproduction), baselineSource = replaced.BaselineSource,
                recognizer = new { window.SessionId, window.Frequency, window.FromTicks, window.ToTicks, window.Complete,
                    window.States, window.CaptureStateIds, window.Inputs },
                capture = new { request.TriggerTicks, request.Ticks, request.TimeSource, request.TriggerKind, request.ViewPending,
                    layerName = reproduction.Layer.Name, layerId = reproduction.Layer.Id, layer = reproduction.Layer,
                    replacedLayer = replaced.Layer, layerMapping = "memolineReproduction", request.View, request.Operations,
                    saveDispatchedTicks = reproduction.SavedTicks },
                memolineOperation = operation,
                search = new { tileSize = LayerDiff.TileSize, outsideDownsampleFactor = 0, coarseWidth = 0, coarseHeight = 0,
                    outsideInspection = "full-resolution every pixel", evidence.Warnings,
                    fullResolutionComparedPixels = diff.FullResolutionComparedPixels, coarseComparedPixels = diff.CoarseComparedPixels,
                    changedPixels = diff.ChangedPixels },
                predictedMatrix = diff.PredictedMatrix, labels = diff.Labels, images = diff.Images, canvasPreviewImage,
                imageConvention = "image is one original-RGBA now patch containing only pixels changed versus after, at full-canvas bounds; afterImage, maskImage and differenceImage are auxiliary; white mask pixels include erasure"
            };
            File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(packet)!); Directory.Move(stage, packet); published = true;
            return new(nowPath, packet, false, diff.Images.Length, diff.Labels.Length);
        }
        finally { if (!published) DeleteOwnDirectory(stage); }
    }

    public ChainVerification VerifyChain(string layerUuid)
    {
        var files = Directory.Exists(Path.Combine(Root, "packets"))
            ? Directory.EnumerateFiles(Path.Combine(Root, "packets"), "manifest.json", SearchOption.AllDirectories) : [];
        var manifests = new List<(string Directory, JsonElement Root, long SortTicks, bool IsDiff)>();
        foreach (var file in files)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement.Clone();
                // An operation packet joins two layers' chains; each chain is proven from its own snapshots.
                if (J.Text(J.Get(root, "capture"), "triggerKind") == "memolineOperation") continue;
                var after = J.Get(root, "after");
                var layer = after.TryGetProperty("layer", out var layerProperty) ? layerProperty : default;
                string uuid = layer.ValueKind == JsonValueKind.Object ? J.Text(layer, "uuid") ?? string.Empty : string.Empty;
                if (!string.Equals(uuid, layerUuid, StringComparison.OrdinalIgnoreCase)) continue;
                var now = J.Get(root, "now");
                long ticks = J.Tick(now, "triggerTicks", J.Tick(after, "triggerTicks", long.MaxValue));
                manifests.Add((Path.GetDirectoryName(file)!, root, ticks, now.ValueKind == JsonValueKind.Object));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { return new(false, Path.GetDirectoryName(file), null, null, "committed manifest cannot be read: " + ex.Message, "ChainBroken"); }
        }
        manifests.Sort((a, b) => a.SortTicks.CompareTo(b.SortTicks));
        var baselines = _baselines.Values.Where(snapshot => string.Equals(snapshot.Layer.Uuid, layerUuid, StringComparison.OrdinalIgnoreCase)
            && TrustedBaselineSource(snapshot.BaselineSource)).ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        var baselineIds = baselines.Keys.ToHashSet(StringComparer.Ordinal);
        if (baselineIds.Count == 0)
            return new(false, manifests.FirstOrDefault().Directory, null, null, "layer has no proven baseline", "NoBaseline");
        (long Width, long Height, string Uuid, string Session, long Generation)? signature = null;
        string? expected = null;
        foreach (var packet in manifests)
        {
            var after = J.Get(packet.Root, "after");
            if (!packet.IsDiff) continue;
            string actualAfter = J.Text(after, "id") ?? string.Empty;
            var now = J.Get(packet.Root, "now");
            if (J.Text(packet.Root, "comparison") != "fullResolution/v2")
                return new(false, packet.Directory, null, null, "packet uses a lossy or unknown image comparison", "ChainLossy");
            string afterUuid = J.Text(J.Get(after, "layer"), "uuid") ?? string.Empty;
            string nowUuid = J.Text(J.Get(now, "layer"), "uuid") ?? string.Empty;
            var currentSignature = (J.Tick(after, "width", -1), J.Tick(after, "height", -1), afterUuid,
                J.Text(after, "sessionId") ?? string.Empty, J.Tick(after, "generation", -1));
            var nowSignature = (J.Tick(now, "width", -1), J.Tick(now, "height", -1), nowUuid,
                J.Text(now, "sessionId") ?? string.Empty, J.Tick(now, "generation", -1));
            if (currentSignature != nowSignature)
                return new(false, packet.Directory, null, null, "after/now metadata is inconsistent", "ChainBroken");
            if (signature is null) signature = currentSignature;
            else if (signature.Value != currentSignature)
                return new(false, packet.Directory, null, null, "history metadata changed within one layer chain", "ChainBroken");
            if (expected is null && !baselineIds.Contains(actualAfter))
            {
                var expectedBaseline = baselineIds.Order(StringComparer.Ordinal).FirstOrDefault();
                return new(false, packet.Directory, expectedBaseline, actualAfter,
                    "first diff does not reference a registered baseline", "ChainBroken");
            }
            if (expected is null && baselines.TryGetValue(actualAfter, out var baseline)
                && (baseline.Width != currentSignature.Item1 || baseline.Height != currentSignature.Item2
                    || !string.Equals(baseline.Layer.Uuid, afterUuid, StringComparison.OrdinalIgnoreCase)
                    || baseline.SessionId != currentSignature.Item4 || baseline.Generation != currentSignature.Item5
                    || baseline.TriggerTicks != J.Tick(after, "triggerTicks", -1)))
                return new(false, packet.Directory, baseline.Id, actualAfter, "first diff metadata differs from its registered baseline", "ChainBroken");
            if (expected is not null && !string.Equals(expected, actualAfter, StringComparison.Ordinal))
                return new(false, packet.Directory, expected, actualAfter, "after.id does not match the previous now.id", "ChainBroken");
            expected = J.Text(now, "id") ?? string.Empty;
        }
        return new(true, null, null, null, null);
    }

    private void PersistIndex(SnapshotPair active)
    {
        string temp = Path.Combine(Root, "current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new SnapshotHistoryIndex(active.After, active.Now, _layerStacks, _baselineIds, _baselines), Json));
            File.Move(temp, Path.Combine(Root, "current.json"), overwrite: true);
        }
        finally { SaveCapture.TryDelete(temp); }
    }
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
            width, height, layer, metadata.Clone(), currentStates, request.TriggerTicks, previous?.BaselineSource);
        string stage = Path.Combine(Root, ".pending-" + id), packet = Path.Combine(Root, "packets", id);
        string imagePath = Path.Combine(Root, snapshot.ImageFile);
        string indexTemp = Path.Combine(Root, "current-" + id + ".tmp");
        bool movedImage = false, published = false, committed = false;
        Directory.CreateDirectory(stage);
        try
        {
            token.ThrowIfCancellationRequested();
            using var timing = StepTiming.Start("history", baseline ? "advance (baseline)" : "advance (diff)");
            // The locator owns a bounded thumbnail of the complete current
            // layer, so historical locations survive full snapshot rotation.
            const string canvasPreviewImage = "canvas-preview.png";
            // The thumbnail decodes the capture again and resamples it for about half a second: beside the comparison.
            var thumbnail = Task.Run(() => CanvasThumbnail.Save(result.PngPath, Path.Combine(stage, canvasPreviewImage), new(width, height), token), token);
            LayerDiffResult? diff;
            try { diff = baseline ? null : LayerDiff.Compare(Path.Combine(Root, previous!.ImageFile), result.PngPath, evidence, stage, token); }
            catch { try { thumbnail.Wait(CancellationToken.None); } catch (Exception) { } throw; }
            thumbnail.GetAwaiter().GetResult();
            var labels = diff?.Labels ?? LayerDiff.Describe(evidence, new(width, height));
            // Historical packets own their cropped before/now pixels. Each layer
            // rotates its own full-size sources independently.
            object Descriptor(LayerSnapshot s) => new { s.Id, s.SessionId, s.Generation, s.TriggerTicks, s.BaselineSource,
                saveDispatchedTicks = s.SavedTicks, s.SavedTicks, s.Width, s.Height, s.Layer, s.RasterMetadata, s.RecognizerStates };
            var manifest = new
            {
                schema = "dirty-matrix-image-diff/v1", comparison = "fullResolution/v2", kind = baseline ? "baseline" : "diff", id,
                after = Descriptor(baseline ? snapshot : previous!), now = baseline ? null : Descriptor(snapshot),
                baselineSource = (baseline ? snapshot : previous!).BaselineSource,
                recognizer = new { window.SessionId, window.Frequency, window.FromTicks, window.ToTicks, window.Complete,
                    window.States, window.CaptureStateIds, window.Inputs },
                capture = new { request.TriggerTicks, request.Ticks, request.TimeSource, request.TriggerKind, request.ViewPending,
                    layerName = layer.Name, layerId = layer.Id, layer,
                    observedLayerName = request.LayerName, initialLayerHint = request.Layer,
                    layerMapping = "currentLayerNameToSavedClipId", request.View,
                    request.Operations, saveDispatchedTicks = result.SaveDispatchedTicks, result.ControlResponse },
                search = new { tileSize = LayerDiff.TileSize, outsideDownsampleFactor = 0,
                    coarseWidth = 0, coarseHeight = 0,
                    outsideInspection = "full-resolution every pixel", evidence.Warnings,
                    fullResolutionComparedPixels = diff?.FullResolutionComparedPixels ?? 0,
                    coarseComparedPixels = diff?.CoarseComparedPixels ?? 0, changedPixels = diff?.ChangedPixels ?? 0 },
                predictedMatrix = diff?.PredictedMatrix ?? LayerDiff.Predicted(evidence, new(width, height)),
                labels, images = diff?.Images ?? [], canvasPreviewImage,
                imageConvention = "image is one original-RGBA now patch containing only pixels changed versus after, at full-canvas bounds; afterImage, maskImage and differenceImage are auxiliary; white mask pixels include erasure"
            };
            File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
            var next = baseline ? new SnapshotPair(snapshot, null) : new SnapshotPair(previous, snapshot);
            nextStacks[layer.Id] = next;
            File.WriteAllText(indexTemp, JsonSerializer.Serialize(new SnapshotHistoryIndex(next.After, next.Now, nextStacks, _baselineIds, _baselines), Json));
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

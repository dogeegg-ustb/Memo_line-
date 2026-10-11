using System.Drawing;
using System.Text.Json;

namespace MemolineDemo.Editing;

/// <summary>Current native SCT observation; all UI distances use physical screen pixels.</summary>
internal sealed class CanvasAffineTransform
{
    private readonly double[] _screenToCanvas, _canvasToScreen;
    internal Size CanvasSize { get; }
    internal Rectangle? Viewport { get; }
    internal string SessionId { get; }

    /// <summary>The view as it is now, before an editing boundary has confirmed the session and canvas size.</summary>
    internal CanvasAffineTransform(JsonElement evidence) : this(evidence, null, null) { }

    internal CanvasAffineTransform(JsonElement evidence, string? expectedSession, Size? expectedCanvas)
    {
        SessionId = Text(evidence, "sessionId") ?? throw new InvalidDataException("画布矩阵缺少会话身份。");
        if (expectedSession is not null && SessionId != expectedSession) throw new InvalidDataException("画布矩阵不属于当前录制会话。");
        if (Get(evidence, "data").ValueKind == JsonValueKind.Object) evidence = Get(evidence, "data");
        // Both callers consume the same-session, fresh active-request result.
        // causalAmbiguous describes attribution to historical input, not the validity
        // of this current observation. Historical replay enforces its own causal anchor.
        if (Text(evidence, "status") is not ("changed" or "unchanged") ||
            Text(evidence, "module") is { } module && module != "canvasViewState")
            throw new InvalidDataException("画布视图状态尚未确认。");
        var state = Get(evidence, "state"); var transform = Get(state, "transform");
        var matrices = Get(transform, "matrices");
        if (Text(matrices, "convention") != "sct-affine-row-major/v1" || Text(matrices, "canvasSpace") != "normalized")
            throw new InvalidDataException("选择区域需要已发布的原生归一化画布矩阵。");
        if (Get(transform, "canvasPixelWidth").ValueKind != JsonValueKind.Number ||
            Get(transform, "canvasPixelHeight").ValueKind != JsonValueKind.Number ||
            !Get(transform, "canvasPixelWidth").TryGetInt32(out int width) ||
            !Get(transform, "canvasPixelHeight").TryGetInt32(out int height) ||
            width <= 0 || height <= 0 || expectedCanvas is { } canvas && new Size(width, height) != canvas)
            throw new InvalidDataException("画布矩阵尺寸与当前编辑会话不一致。");
        CanvasSize = new(width, height);
        _screenToCanvas = ReadMatrix(matrices, "screenToCanvas");
        _canvasToScreen = ReadMatrix(matrices, "canvasToScreen");
        if (!Invertible(_screenToCanvas) || !Invertible(_canvasToScreen) ||
            !IdentityProduct(_screenToCanvas, _canvasToScreen) || !IdentityProduct(_canvasToScreen, _screenToCanvas))
            throw new InvalidDataException("画布正逆矩阵不可逆或不一致。");
        var origin = Get(state, "canvasOriginScreenPx");
        if (Get(origin, "x").TryNumber(out double x) && Get(origin, "y").TryNumber(out double y) &&
            (Math.Abs(x - _canvasToScreen[2]) > .5 || Math.Abs(y - _canvasToScreen[5]) > .5))
            throw new InvalidDataException("画布原点与原生矩阵不一致。");
        var raw = Get(evidence, "rawResult");
        if (Get(raw, "success").ValueKind == JsonValueKind.False) throw new InvalidDataException("画布矩阵识别失败。");
        Viewport = StrokeReplay.CanvasViewSnapshot.ParseRectangle(Get(raw, "canvasWindowRoiScreenPx"));
    }

    /// <summary>Same session, canvas and on-screen placement (within a quarter pixel at the canvas corners).</summary>
    internal bool SameView(CanvasAffineTransform other)
        => SessionId == other.SessionId && CanvasSize == other.CanvasSize &&
           new PointF[] { new(0, 0), new(CanvasSize.Width, 0), new(0, CanvasSize.Height) }.All(point =>
           { var a = CanvasToScreen(point); var b = other.CanvasToScreen(point); return Math.Abs(a.X - b.X) <= .25 && Math.Abs(a.Y - b.Y) <= .25; });

    internal PointF ScreenToCanvas(PointF point) => Map(_screenToCanvas, point, CanvasSize.Width, CanvasSize.Height);
    internal PointF CanvasToScreen(PointF point) => Map(_canvasToScreen,
        new(point.X / CanvasSize.Width, point.Y / CanvasSize.Height), 1, 1);
    internal bool IsCanvasPoint(PointF screen)
    {
        if (Viewport is { } viewport && !viewport.Contains(Point.Round(screen))) return false;
        var p = ScreenToCanvas(screen);
        return p.X >= 0 && p.Y >= 0 && p.X <= CanvasSize.Width && p.Y <= CanvasSize.Height;
    }
    private static PointF Map(double[] m, PointF p, int width, int height)
    {
        double x = (m[0] * p.X + m[1] * p.Y + m[2]) * width;
        double y = (m[3] * p.X + m[4] * p.Y + m[5]) * height;
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue)
            throw new InvalidDataException("画布坐标映射超出有效范围。");
        return new((float)x, (float)y);
    }
    private static double[] ReadMatrix(JsonElement matrices, string key)
    {
        var value = Get(matrices, key);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 6) throw new InvalidDataException("原生矩阵需要六个有限数值。");
        var result = new double[6];
        for (int i = 0; i < 6; i++)
            if (!value[i].TryNumber(out result[i])) throw new InvalidDataException("原生矩阵含无效数值。");
        return result;
    }
    private static bool Invertible(double[] m)
    {
        double scale = new[] { Math.Abs(m[0]), Math.Abs(m[1]), Math.Abs(m[3]), Math.Abs(m[4]) }.Max();
        if (scale == 0) return false;
        double det = m[0] / scale * (m[4] / scale) - m[1] / scale * (m[3] / scale);
        if (!double.IsFinite(det) || det == 0) return false;
        double a = m[4] / scale / det / scale, b = -m[1] / scale / det / scale;
        double c = -m[3] / scale / det / scale, d = m[0] / scale / det / scale;
        return new[] { a, b, c, d, -(a * m[2] + b * m[5]), -(c * m[2] + d * m[5]) }.All(double.IsFinite);
    }
    private static bool IdentityProduct(double[] a, double[] b)
    {
        double[] product = [a[0] * b[0] + a[1] * b[3], a[0] * b[1] + a[1] * b[4],
            a[0] * b[2] + a[1] * b[5] + a[2], a[3] * b[0] + a[4] * b[3], a[3] * b[1] + a[4] * b[4],
            a[3] * b[2] + a[4] * b[5] + a[5]];
        double[] expected = [1, 0, 0, 0, 1, 0];
        return product.Select((v, i) => double.IsFinite(v) && Math.Abs(v - expected[i]) <= 1e-7).All(v => v);
    }
    internal static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    internal static string? Text(JsonElement value, string key) => Get(value, key) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;
}

internal static class CanvasJsonNumber
{
    internal static bool TryNumber(this JsonElement value, out double number)
    { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number); }
}

internal sealed record CanvasImageCandidate(DirtyImage Image, string LayerName, long TriggerTicks, bool CanSelect, string Reason)
{
    internal string? SourceSessionId { get; init; }
    internal int SessionOrder { get; init; }
}
internal sealed record CanvasStrokePath(EditableStrokeKey Key, IReadOnlyList<PointF[]> Paths);

/// <summary>Pure selection state. No CSP actions, files, clipboard, or injected input.</summary>
internal sealed class CanvasInteraction
{
    private EditSession? _session;
    private IReadOnlyList<PointF>? _polygon;
    private readonly Dictionary<EditableStrokeKey, CanvasStrokePath> _paths = [];
    private readonly HashSet<EditableStrokeKey> _selected = [], _touched = [];
    private PointF? _sweepPrevious;
    internal CanvasAffineTransform Transform { get; }
    internal PixelMask? RegionSelection { get; private set; }
    internal IReadOnlyList<CanvasImageCandidate> Candidates { get; private set; } = [];
    internal DirtyImageKey? ChosenImage { get; private set; }
    internal IReadOnlySet<EditableStrokeKey> SelectedEvents => _selected;
    internal IReadOnlyList<EditableStroke> EligibleStrokes { get; private set; } = [];
    internal IReadOnlyList<CanvasStrokePath> SelectedPaths => _selected.Select(k => _paths[k]).ToArray();
    internal event EventHandler? RegionChanged;
    internal event EventHandler? SelectionChanged;

    internal CanvasInteraction(EditSession session) : this(TransformFor(session)) => _session = session;

    /// <summary>A frozen view the user can already select on; the editing session is attached once it is ready.</summary>
    internal CanvasInteraction(CanvasAffineTransform transform) => Transform = transform;

    internal bool IsReady => _session is not null;
    private EditSession Session => _session ?? throw new InvalidOperationException("编辑边界尚未建立，请稍候。");

    /// <summary>Attaches the session built in the background; a region chosen meanwhile is evaluated now.</summary>
    internal void Attach(EditSession session)
    {
        if (_session is not null) throw new InvalidOperationException("编辑会话已经建立。");
        if (!TransformFor(session).SameView(Transform))
            throw new InvalidDataException("冻结之后画布视图或录制会话发生了变化，请重新冻结画布。");
        _session = session;
        if (_polygon is not null) SetRegion(_polygon);
    }

    private static CanvasAffineTransform TransformFor(EditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var current = session.CurrentLayer is { } layer && session.Index.Layers.TryGetValue(layer, out var found) ? found.Current : null;
        current ??= session.Index.Layers.Values.Select(l => l.Current).FirstOrDefault(i => i is not null);
        if (current is null) throw new InvalidDataException("编辑会话没有已确认的整层画布捕获。");
        if (session.CurrentLayer is { } target && target.SessionId != session.LiveView.SessionId)
            throw new InvalidDataException("当前编辑图层不属于活跃录制会话。");
        return new(session.CanvasViewEvidence, session.LiveView.SessionId, new Size(current.Width, current.Height));
    }

    internal void SetRegion(IReadOnlyList<PointF> screenPolygon)
    {
        if (screenPolygon.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y))) throw new InvalidDataException("选区含无效屏幕坐标。");
        var mask = CanvasSelectionGeometry.Polygon(Transform.CanvasSize, screenPolygon.Select(Transform.ScreenToCanvas).ToArray());
        if (mask.IsEmpty) throw new InvalidDataException("请选择至少包含一个画布像素的区域。");
        RegionSelection = mask; _polygon = screenPolygon.ToArray();
        Candidates = _session is null ? [] : _session.Index.Layers.Values.SelectMany(layer => layer.Packets.OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks)
            .SelectMany(packet => packet.Images.Where(image => !image.IsBaseline && image.Mask.Intersects(mask))
                .Select(image => Candidate(layer, packet, image))))
            .OrderBy(c => c.LayerName, StringComparer.Ordinal).ThenBy(c => c.SessionOrder).ThenBy(c => c.TriggerTicks).ThenBy(c => c.Image.Key.ImageId, StringComparer.Ordinal).ToArray();
        ResetEvents(); RegionChanged?.Invoke(this, EventArgs.Empty); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    internal void ClearSelection()
    {
        RegionSelection = null; _polygon = null; Candidates = []; ResetEvents();
        RegionChanged?.Invoke(this, EventArgs.Empty); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ResetEvents()
    { ChosenImage = null; _selected.Clear(); _paths.Clear(); EligibleStrokes = []; EndSweep(); }

    private CanvasImageCandidate Candidate(LayerIndex layer, LayerPacket packet, DirtyImage image)
    {
        string name = CanvasAffineTransform.Text(CanvasAffineTransform.Get(packet.NowDescriptor, "layer"), "name") ?? layer.Layer.Uuid;
        var issues = layer.Issues.Where(i => i.Image is null || i.Image == image.Key).ToArray();
        string reason = string.Join("；", issues.Select(i => $"{i.Code}: {i.Message}"));
        if (!layer.ChainValid && reason.Length == 0) reason = "ChainBroken: 图层历史链不完整。";
        if (packet.CanvasWidth != Transform.CanvasSize.Width || packet.CanvasHeight != Transform.CanvasSize.Height)
            reason = Join(reason, "历史图片的画布尺寸与当前画布不一致。");
        if (!image.FullResolution) reason = Join(reason, "ChainLossy: 图片不是全分辨率捕获。");
        if (image.HasUnreplayableEvents) reason = Join(reason, "UnreplayableEventInMatrix: 图片含不可重放事件。");
        if (!image.Events.Any(k => Session.Index.Strokes.TryGetValue(k, out var s) && s.Eligible && !s.IsUndone && s.Layer == image.Key.Layer))
            reason = Join(reason, "图片中没有可选择的完整笔画。");
        return new(image, name, packet.TriggerTicks, reason.Length == 0, reason)
        { SourceSessionId = packet.SourceLayer?.SessionId ?? packet.Layer.SessionId, SessionOrder = packet.SessionOrder };
    }
    private static string Join(string a, string b) => a.Length == 0 ? b : a + "；" + b;

    internal void SelectImage(DirtyImageKey image)
    {
        var candidate = Candidates.FirstOrDefault(c => c.Image.Key == image) ?? throw new InvalidDataException("请选择与当前选区相交的单张脏矩阵。");
        if (!candidate.CanSelect) throw new InvalidDataException(candidate.Reason);
        var eligible = candidate.Image.Events.Distinct().Select(k => Session.Index.Strokes.GetValueOrDefault(k))
            .Where(s => s is { Eligible: true, IsUndone: false } && s.Layer == image.Layer).Select(s => s!).OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ToArray();
        var paths = eligible.Select(BuildPath).ToArray();
        ResetEvents(); ChosenImage = image; EligibleStrokes = eligible;
        foreach (var path in paths) _paths.Add(path.Key, path);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private CanvasStrokePath BuildPath(EditableStroke stroke)
    {
        var lines = new List<PointF[]>(); var points = new List<PointF>();
        void Flush() { if (points.Count > 0) lines.Add(points.ToArray()); points.Clear(); }
        foreach (var segment in stroke.CoverageLabels.OrderBy(l => l.FromTicks).ThenBy(l => l.ToTicks)
            .SelectMany(l => l.Segments).Distinct())
        {
            var start = Transform.CanvasToScreen(new((float)segment.StartX, (float)segment.StartY));
            var end = Transform.CanvasToScreen(new((float)segment.EndX, (float)segment.EndY));
            if (points.Count > 0 && points[^1] != start) Flush();
            if (points.Count == 0) points.Add(start);
            if (points[^1] != end) points.Add(end);
        }
        Flush();
        if (lines.Count == 0)
        {
            var view = Session.Source(stroke.Key.SessionId).History.LastOrDefault(h => h.Module == "canvasViewState" && h.Anchor < stroke.BeginEventId)?.Data ?? default;
            if (!Footprints.TryMapContactSamples(stroke.Samples, view, Transform.CanvasSize.Width, Transform.CanvasSize.Height, out var mapped))
                throw new InvalidDataException($"笔画 {stroke.BeginEventId} 缺少完整画布折线。");
            lines.Add(mapped.Select(p => Transform.CanvasToScreen(new((float)p.X, (float)p.Y))).ToArray());
        }
        return new(stroke.Key, lines);
    }
    internal IReadOnlyList<CanvasStrokePath> HoverPaths(PointF screen) => _paths.Values.Where(p => DistanceToPath(screen, p) <= 48).ToArray();
    /// <summary>White lines for display only: a stroke whose path cannot be drawn is left out instead of
    /// failing the selection it belongs to.</summary>
    internal IReadOnlyList<CanvasStrokePath> PathsForEvents(IEnumerable<EditableStrokeKey> keys)
    {
        var paths = new List<CanvasStrokePath>();
        foreach (var key in keys.Distinct())
            if (Session.Index.Strokes.TryGetValue(key, out var stroke) && stroke.Eligible && !stroke.IsUndone)
                try { paths.Add(BuildPath(stroke)); } catch (InvalidDataException) { }
        return paths;
    }
    internal void SetSelectedEvents(IEnumerable<EditableStrokeKey> keys)
    {
        var selected = keys.Distinct().ToArray();
        if (selected.Any(k => !_paths.ContainsKey(k))) throw new InvalidDataException("只能选择当前单张图片关联的合格完整笔画。");
        _selected.Clear(); _selected.UnionWith(selected); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    internal void BeginSweep(PointF screen)
    {
        if (ChosenImage is null) return;
        _touched.Clear(); _sweepPrevious = screen; Sweep(screen, screen);
    }
    internal void ContinueSweep(PointF screen)
    { if (_sweepPrevious is { } previous) { Sweep(previous, screen); _sweepPrevious = screen; } }
    internal void EndSweep() { _sweepPrevious = null; _touched.Clear(); }
    private void Sweep(PointF start, PointF end)
    {
        bool changed = false;
        foreach (var path in _paths.Values)
            if (!_touched.Contains(path.Key) && path.Paths.Any(points => Enumerable.Range(0, points.Length)
                .Any(i => SegmentDistance(start, end, points[Math.Max(0, i - 1)], points[i]) <= 12)))
            {
                _touched.Add(path.Key); if (!_selected.Remove(path.Key)) _selected.Add(path.Key); changed = true;
            }
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private static double DistanceToPath(PointF p, CanvasStrokePath path) => path.Paths.SelectMany(points =>
        Enumerable.Range(0, points.Length).Select(i => PointSegmentDistance(p, points[Math.Max(0, i - 1)], points[i]))).DefaultIfEmpty(double.PositiveInfinity).Min();
    private static double PointSegmentDistance(PointF p, PointF a, PointF b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
        double t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        double x = p.X - a.X - t * dx, y = p.Y - a.Y - t * dy;
        return Math.Sqrt(x * x + y * y);
    }
    private static double SegmentDistance(PointF a, PointF b, PointF c, PointF d)
    {
        static double Cross(PointF u, PointF v, PointF w) => (double)(v.X - u.X) * (w.Y - u.Y) - (double)(v.Y - u.Y) * (w.X - u.X);
        double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
        if (((abC > 0 && abD < 0) || (abC < 0 && abD > 0)) && ((cdA > 0 && cdB < 0) || (cdA < 0 && cdB > 0))) return 0;
        return new[] { PointSegmentDistance(a, c, d), PointSegmentDistance(b, c, d), PointSegmentDistance(c, a, b), PointSegmentDistance(d, a, b) }.Min();
    }
}

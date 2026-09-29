using System.Drawing;

namespace CSPevent;

internal sealed record HighlightEvidence(
    string State,
    double ChangedFraction,
    int? Left,
    int? Right,
    int? Top = null,
    int? Bottom = null,
    Rectangle? Bounds = null)
{
    internal bool HasSelectedSpan => (State == "prehighlight" || State == "highlight_changed") &&
        Left.HasValue && Right.HasValue;
    internal bool HasBounds => Bounds.HasValue && Bounds.Value.Width >= 6 && Bounds.Value.Height >= 6;
}

internal sealed record Highlight2DEvidence(
    string State,
    double ChangedFraction,
    Rectangle? Bounds,
    Point? Center,
    int? Left,
    int? Right,
    double DynamicEnergy = 0.0,
    string? SelectionReason = null)
{
    internal bool HasSelectedSpan => (State == "prehighlight" || State == "highlight_changed") &&
        Left.HasValue && Right.HasValue;

    internal bool Has2DBounds => (State == "prehighlight" || State == "highlight_changed") &&
        Bounds.HasValue && Bounds.Value.Width >= 6 && Bounds.Value.Height >= 6;

    internal HighlightEvidence To1D() => new(State, ChangedFraction, Left, Right, Bounds?.Top, Bounds?.Bottom, Bounds);
}

/// <summary>
/// 超低分辨率差分矩阵：用于将前后截屏按粗粒度块降采样对比，
/// 提取画面动态能量场，从多个高光候选（常驻选中的Tab/图层 vs 刚点击的按钮）中精准判定哪个是新按的。
/// </summary>
internal sealed class LowResDiffMap
{
    internal const int DefaultBlockSize = 8;
    internal int BlockSize { get; }
    internal int GridWidth { get; }
    internal int GridHeight { get; }
    internal double[,] Energy { get; }
    internal double TotalEnergy { get; }

    internal LowResDiffMap(int blockSize, int gridWidth, int gridHeight, double[,] energy, double totalEnergy)
    {
        BlockSize = blockSize;
        GridWidth = gridWidth;
        GridHeight = gridHeight;
        Energy = energy;
        TotalEnergy = totalEnergy;
    }

    internal static LowResDiffMap Compute(Bitmap? img1, Bitmap? img2, int blockSize = DefaultBlockSize)
    {
        if (img1 is null || img2 is null || img1.Size != img2.Size || img1.Width < blockSize || img1.Height < blockSize)
        {
            return new LowResDiffMap(blockSize, 0, 0, new double[0, 0], 0);
        }

        int gw = img1.Width / blockSize;
        int gh = img1.Height / blockSize;
        if (gw <= 0 || gh <= 0) return new LowResDiffMap(blockSize, 0, 0, new double[0, 0], 0);

        var energy = new double[gh, gw];
        double total = 0;

        for (int gy = 0; gy < gh; gy++)
        {
            int yStart = gy * blockSize;
            int yEnd = Math.Min(yStart + blockSize, img1.Height);
            for (int gx = 0; gx < gw; gx++)
            {
                int xStart = gx * blockSize;
                int xEnd = Math.Min(xStart + blockSize, img1.Width);

                int count = 0;
                double sumDiff = 0;

                // 超低分辨率快速采样（步长为2，单张全图比较耗时 < 0.1ms）
                for (int y = yStart + 1; y < yEnd; y += 2)
                {
                    for (int x = xStart + 1; x < xEnd; x += 2)
                    {
                        Color c1 = img1.GetPixel(x, y);
                        Color c2 = img2.GetPixel(x, y);
                        int delta = Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B);
                        sumDiff += delta;
                        count++;
                    }
                }

                double avgDiff = count > 0 ? sumDiff / count : 0;
                // 差分能量门禁：阈值25过滤抗锯齿抖动，高差分对应动态点击/状态改变
                double val = avgDiff >= 25 ? Math.Clamp((avgDiff - 25) / 50.0, 0.0, 1.0) : 0.0;
                energy[gy, gx] = val;
                total += val;
            }
        }

        return new LowResDiffMap(blockSize, gw, gh, energy, total);
    }

    internal double GetEnergyInRect(Rectangle rect)
    {
        if (GridWidth == 0 || GridHeight == 0) return 0.0;
        int gxMin = Math.Clamp(rect.Left / BlockSize, 0, GridWidth - 1);
        int gxMax = Math.Clamp(rect.Right / BlockSize, 0, GridWidth - 1);
        int gyMin = Math.Clamp(rect.Top / BlockSize, 0, GridHeight - 1);
        int gyMax = Math.Clamp(rect.Bottom / BlockSize, 0, GridHeight - 1);

        double sum = 0;
        int blocks = 0;
        for (int gy = gyMin; gy <= gyMax; gy++)
        {
            for (int gx = gxMin; gx <= gxMax; gx++)
            {
                sum += Energy[gy, gx];
                blocks++;
            }
        }
        return blocks > 0 ? sum / blocks : 0.0;
    }
}

internal static class HighlightAnalyzer
{
    internal static HighlightEvidence Analyze(Bitmap before, Bitmap? after, int clickX)
    {
        HighlightEvidence prehighlight = DetectPrehighlight(before, clickX);
        if (after is null || before.Size != after.Size)
            return prehighlight.HasSelectedSpan ? prehighlight :
                new HighlightEvidence("post_capture_unavailable", 0, null, null);
        int width = before.Width;
        int height = before.Height;
        if (width < 20 || height < 12)
            return new HighlightEvidence("unknown", 0, null, null);

        var changed = new bool[width];
        var fractions = new double[width];
        for (int x = 0; x < width; x += 2)
        {
            int changedCount = 0;
            int samples = 0;
            for (int y = 3; y < height - 3; y += 3)
            {
                Color oldColor = before.GetPixel(x, y);
                Color newColor = after.GetPixel(x, y);
                int delta = Math.Abs(oldColor.R - newColor.R) +
                    Math.Abs(oldColor.G - newColor.G) + Math.Abs(oldColor.B - newColor.B);
                if (delta >= 65) changedCount++;
                samples++;
            }
            double fraction = samples == 0 ? 0 : (double)changedCount / samples;
            fractions[x] = fraction;
            if (x + 1 < width) fractions[x + 1] = fraction;
            changed[x] = fraction >= 0.18;
            if (x + 1 < width) changed[x + 1] = changed[x];
        }

        for (int x = 2; x < width - 2; x++)
            if (!changed[x] && changed[x - 2] && changed[x + 2]) changed[x] = true;

        int bestLeft = -1, bestRight = -1;
        double bestStrength = 0;
        int bestDistance = int.MaxValue;
        for (int x = 0; x < width;)
        {
            if (!changed[x]) { x++; continue; }
            int left = x;
            while (x < width && changed[x]) x++;
            int right = x;
            int gap = clickX < left ? left - clickX : clickX >= right ? clickX - right + 1 : 0;
            double strength = fractions.Skip(left).Take(right - left).Average();
            if (strength > bestStrength + 0.03 ||
                (Math.Abs(strength - bestStrength) <= 0.03 && gap < bestDistance))
            {
                bestStrength = strength;
                bestDistance = gap;
                bestLeft = left;
                bestRight = right;
            }
        }
        int minSpan = Math.Max(10, width / 12);
        if (bestLeft < 0 || bestRight - bestLeft < minSpan)
            return prehighlight.HasSelectedSpan ? prehighlight :
                new HighlightEvidence("no_highlight_change", 0, null, null);
        double changedFraction = fractions.Skip(bestLeft).Take(bestRight - bestLeft).Average();
        if (changedFraction < 0.18)
            return prehighlight.HasSelectedSpan ? prehighlight :
                new HighlightEvidence("no_highlight_change", changedFraction, null, null);
        return new HighlightEvidence("highlight_changed", changedFraction,
            bestLeft, bestRight);
    }

    /// <summary>
    /// 核心 2D 高光与多选判定分析引擎：
    /// 在判定高光前，首先与前一个截图/后一个截图进行超低分辨率差分比较，
    /// 结合水平行隔离（防止按钮与常驻画布Tab合并）与动态能量评估，
    /// 从画面中多个可能的高光中精准判定“哪一个是新按的”。
    /// </summary>
    internal static Highlight2DEvidence Analyze2D(
        Bitmap before,
        Bitmap? after,
        int clickX,
        int clickY,
        Bitmap? previous = null)
    {
        int width = before.Width;
        int height = before.Height;
        if (width < 8 || height < 8)
            return new Highlight2DEvidence("unknown", 0, null, null, null, null);

        // 1. 超低分辨率差分比较：在判定高光前，计算前后两帧的块级动态能量场
        LowResDiffMap diffAfter = LowResDiffMap.Compute(before, after);
        LowResDiffMap diffPrev = LowResDiffMap.Compute(previous, before);

        // 2. 检测水平隔离分割线（如工具栏与文档标签栏之间亮度 < 50 的水平深色分隔线）
        List<int> separators = DetectHorizontalSeparators(before);
        var (bandMinY, bandMaxY) = GetRowBand(clickY, separators, height);

        // 3. 提取全图所有候选高光连通块（含静态预选高光与前后帧差分高光）
        List<HighlightCluster> candidates = ExtractHighlightClusters(
            before, after, separators);

        if (candidates.Count == 0)
        {
            // 回退到 1D 线性分析
            HighlightEvidence fallback = Analyze(before, after, clickX);
            Rectangle? fallbackRect = fallback.HasSelectedSpan
                ? new Rectangle(fallback.Left!.Value, bandMinY, fallback.Right!.Value - fallback.Left!.Value, bandMaxY - bandMinY + 1)
                : null;

            return new Highlight2DEvidence(
                fallback.State,
                fallback.ChangedFraction,
                fallbackRect,
                fallbackRect is Rectangle rect
                    ? new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2)
                    : new Point(clickX, clickY),
                fallback.Left,
                fallback.Right);
        }

        // 4. 多选仲裁：基于先后对比与动态能量，判定哪个是“新按的”
        HighlightCluster bestCluster = RankAndSelectCluster(
            candidates, diffAfter, diffPrev, clickX, clickY, bandMinY, bandMaxY);

        string finalState = (bestCluster.DynamicEnergy >= 0.1 || bestCluster.FromDiff)
            ? "highlight_changed"
            : "prehighlight";

        double coverage = (double)bestCluster.Pixels / (bestCluster.Rect.Width * bestCluster.Rect.Height);
        return new Highlight2DEvidence(
            finalState,
            coverage,
            bestCluster.Rect,
            bestCluster.Center,
            bestCluster.Rect.Left,
            bestCluster.Rect.Right,
            bestCluster.DynamicEnergy,
            bestCluster.SelectionReason);
    }

    private sealed record HighlightCluster(
        Rectangle Rect,
        int Pixels,
        int BluePixels,
        int GrayPixels,
        int DynamicPixels,
        Point Center,
        bool FromDiff,
        double DynamicEnergy,
        string SelectionReason);

    private static List<int> DetectHorizontalSeparators(Bitmap image)
    {
        var separators = new List<int>();
        int w = image.Width;
        int h = image.Height;
        var rowMean = new double[h];
        var darkFraction = new double[h];

        for (int y = 0; y < h; y++)
        {
            int darkCount = 0;
            int step = Math.Max(1, w / 40);
            int samples = 0;
            double lumSum = 0;
            for (int x = 0; x < w; x += step)
            {
                Color c = image.GetPixel(x, y);
                double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                if (lum < 48) darkCount++;
                lumSum += lum;
                samples++;
            }
            rowMean[y] = samples > 0 ? lumSum / samples : 0;
            darkFraction[y] = samples > 0 ? (double)darkCount / samples : 0;
        }

        // A uniformly dark theme is background, not a separator. A separator must
        // be a narrow dark trough relative to rows on both sides.
        for (int y = 2; y < h - 2; y++)
        {
            if (darkFraction[y] < 0.75) continue;
            double neighbors = (rowMean[y - 2] + rowMean[y + 2]) / 2;
            if (neighbors - rowMean[y] >= 8)
                separators.Add(y);
        }
        return separators;
    }

    private static (int MinY, int MaxY) GetRowBand(int y, List<int> separators, int height)
    {
        int minY = 0;
        int maxY = height - 1;

        foreach (int sep in separators)
        {
            if (sep < y && sep > minY) minY = sep + 1;
            if (sep > y && sep < maxY) { maxY = sep - 1; break; }
        }
        return (minY, maxY);
    }

    private static List<HighlightCluster> ExtractHighlightClusters(
        Bitmap before,
        Bitmap? after,
        List<int> separators)
    {
        int width = before.Width;
        int height = before.Height;
        var separatorSet = new HashSet<int>(separators);

        // 像素点采样判定：高亮（蓝色/悬停灰）与动态变化点
        var hl = new bool[width, height];
        var isBluePixel = new bool[width, height];
        var isGrayPixel = new bool[width, height];
        var isDynamicPixel = new bool[width, height];

        for (int y = 0; y < height; y++)
        {
            if (separatorSet.Contains(y)) continue; // 隔离线不允许连通

            for (int x = 0; x < width; x++)
            {
                Color c1 = before.GetPixel(x, y);
                bool isBlue = IsBlueHighlight(c1);
                bool isHoverGray = IsGrayHighlight(c1);

                bool isDiff = false;
                if (after is not null && after.Size == before.Size)
                {
                    Color c2 = after.GetPixel(x, y);
                    isBlue |= IsBlueHighlight(c2);
                    isHoverGray |= IsGrayHighlight(c2);
                    int delta = Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B);
                    if (delta >= 40)
                    {
                        isDiff = true;
                        isDynamicPixel[x, y] = true;
                    }
                }

                if (isBlue || isHoverGray || isDiff)
                {
                    hl[x, y] = true;
                    isBluePixel[x, y] = isBlue;
                    isGrayPixel[x, y] = isHoverGray;
                }
            }
        }

        // 水平连通空隙桥接（文字笔画或图标内部的细微间隙）
        for (int y = 1; y < height - 1; y++)
        {
            if (separatorSet.Contains(y)) continue;
            for (int x = 2; x < width - 2; x++)
            {
                if (!hl[x, y] && ((hl[x - 1, y] && hl[x + 1, y]) || (hl[x - 2, y] && hl[x + 2, y])))
                    hl[x, y] = true;
            }
        }

        // 2D 连通域聚类（严禁跨越水平分割线）
        var visited = new bool[width, height];
        var clusters = new List<HighlightCluster>();

        for (int y = 0; y < height; y++)
        {
            if (separatorSet.Contains(y)) continue;

            for (int x = 0; x < width; x++)
            {
                if (!hl[x, y] || visited[x, y]) continue;

                int minX = x, maxX = x, minY = y, maxY = y;
                int count = 0;
                int bluePixelCount = 0;
                int grayPixelCount = 0;
                int dynamicPixelCount = 0;
                var queue = new Queue<Point>();
                queue.Enqueue(new Point(x, y));
                visited[x, y] = true;

                while (queue.Count > 0)
                {
                    Point p = queue.Dequeue();
                    count++;
                    if (isBluePixel[p.X, p.Y]) bluePixelCount++;
                    if (isGrayPixel[p.X, p.Y]) grayPixelCount++;
                    if (isDynamicPixel[p.X, p.Y]) dynamicPixelCount++;

                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;

                    // 4-邻域搜索，遇到分隔线停止扩散
                    if (p.X > 0 && hl[p.X - 1, p.Y] && !visited[p.X - 1, p.Y])
                    { visited[p.X - 1, p.Y] = true; queue.Enqueue(new Point(p.X - 1, p.Y)); }
                    if (p.X + 1 < width && hl[p.X + 1, p.Y] && !visited[p.X + 1, p.Y])
                    { visited[p.X + 1, p.Y] = true; queue.Enqueue(new Point(p.X + 1, p.Y)); }

                    if (p.Y > 0 && !separatorSet.Contains(p.Y - 1) && hl[p.X, p.Y - 1] && !visited[p.X, p.Y - 1])
                    { visited[p.X, p.Y - 1] = true; queue.Enqueue(new Point(p.X, p.Y - 1)); }
                    if (p.Y + 1 < height && !separatorSet.Contains(p.Y + 1) && hl[p.X, p.Y + 1] && !visited[p.X, p.Y + 1])
                    { visited[p.X, p.Y + 1] = true; queue.Enqueue(new Point(p.X, p.Y + 1)); }
                }

                int w = maxX - minX + 1;
                int h = maxY - minY + 1;
                if (w >= 10 && h >= 10 && count >= 24)
                {
                    var rect = new Rectangle(minX, minY, w, h);
                    var center = new Point((minX + maxX) / 2, (minY + maxY) / 2);
                    bool fromDiff = dynamicPixelCount >= 12;
                    clusters.Add(new HighlightCluster(rect, count, bluePixelCount,
                        grayPixelCount, dynamicPixelCount, center, fromDiff, 0.0, "extracted"));
                }
            }
        }

        return clusters;
    }

    private static HighlightCluster RankAndSelectCluster(
        List<HighlightCluster> candidates,
        LowResDiffMap diffAfter,
        LowResDiffMap diffPrev,
        int clickX,
        int clickY,
        int clickBandMinY,
        int clickBandMaxY)
    {
        var scored = new List<(HighlightCluster Cluster, double Score)>();
        bool hasSameBandCandidate = candidates.Any(c =>
            c.Rect.Top >= clickBandMinY && c.Rect.Bottom <= clickBandMaxY + 2);

        foreach (var c in candidates)
        {
            Rectangle rect = c.Rect;
            // 计算当前候选块在前后差分矩阵中的动态能量
            double energyAfter = diffAfter.GetEnergyInRect(rect);
            double energyPrev = diffPrev.GetEnergyInRect(rect);
            double maxDynamicEnergy = Math.Max(energyAfter, energyPrev);
            if (c.FromDiff) maxDynamicEnergy = Math.Max(maxDynamicEnergy, 0.5);

            // 是否与点击点处于同一个行区域（未跨越水平深色分割线）
            bool inSameBand = rect.Top >= clickBandMinY && rect.Bottom <= clickBandMaxY + 2;
            if (hasSameBandCandidate && !inSameBand) continue;

            double area = Math.Max(1, rect.Width * rect.Height);
            double blueCoverage = c.BluePixels / area;
            double grayCoverage = c.GrayPixels / area;
            double dynamicCoverage = c.DynamicPixels / area;
            double visualEvidence = Math.Clamp(blueCoverage * 2.0 + grayCoverage * 0.5, 0, 1);
            double dynamicEvidence = Math.Clamp(Math.Max(maxDynamicEnergy, dynamicCoverage), 0, 1);

            // Mouse position only resolves nearby candidates with similar highlight evidence.
            int dx = Math.Max(0, Math.Max(rect.Left - clickX, clickX - rect.Right));
            int dy = Math.Max(0, Math.Max(rect.Top - clickY, clickY - rect.Bottom));
            double distSq = dx * dx + dy * dy;
            double distPenalty = Math.Sqrt(distSq);

            double sizeAdjustment = rect.Width > 110 && rect.Height <= 28 ? -4.0 : 0.0;
            double finalScore = visualEvidence * 100.0 + dynamicEvidence * 120.0 +
                                sizeAdjustment - Math.Min(distPenalty, 80.0) * 0.08;
            string reason = dynamicEvidence >= 0.08
                ? $"highlight_change(visual={visualEvidence:F2},energy={dynamicEvidence:F2})"
                : $"visual_highlight(visual={visualEvidence:F2})";

            var evaluatedCluster = c with {
                DynamicEnergy = maxDynamicEnergy,
                SelectionReason = reason
            };
            scored.Add((evaluatedCluster, finalScore));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored[0].Cluster;
    }

    private static bool IsBlueHighlight(Color c) =>
        c.B >= 85 && c.B - c.R >= 12 && c.B - c.G >= 3;

    private static bool IsGrayHighlight(Color c) =>
        c.R >= 85 && c.R <= 160 && Math.Abs(c.R - c.G) <= 6 &&
        Math.Abs(c.G - c.B) <= 6;

    private static HighlightEvidence DetectPrehighlight(Bitmap image, int clickX)
    {
        int width = image.Width;
        int height = image.Height;
        if (width < 20 || height < 12) return new HighlightEvidence("unknown", 0, null, null);

        var hl = new bool[width];
        for (int x = 0; x < width; x += 2)
        {
            int hlCount = 0;
            int samples = 0;
            for (int y = 3; y < height - 3; y += 3)
            {
                Color c = image.GetPixel(x, y);
                bool isBlue = c.B >= 85 && (c.B - c.R) >= 12 && (c.B - c.G) >= 3;
                bool isHoverGray = c.R >= 85 && c.R <= 160 && Math.Abs(c.R - c.G) <= 6 && Math.Abs(c.G - c.B) <= 6;
                if (isBlue || isHoverGray) hlCount++;
                samples++;
            }
            if (samples > 0 && (double)hlCount / samples >= 0.25)
            {
                hl[x] = true;
                if (x + 1 < width) hl[x + 1] = true;
            }
        }

        for (int x = 2; x < width - 2; x++)
            if (!hl[x] && hl[x - 2] && hl[x + 2]) hl[x] = true;

        int bestLeft = -1, bestRight = -1;
        int distance = int.MaxValue;
        for (int x = 0; x < width;)
        {
            if (!hl[x]) { x++; continue; }
            int left = x;
            while (x < width && hl[x]) x++;
            int right = x;
            int gap = clickX < left ? left - clickX : clickX >= right ? clickX - right + 1 : 0;
            if (gap < distance) { distance = gap; bestLeft = left; bestRight = right; }
        }
        int minSpan = Math.Max(10, width / 12);
        if (bestLeft < 0 || bestRight - bestLeft < minSpan || distance > 8)
            return new HighlightEvidence("no_prehighlight", 0, null, null);

        return new HighlightEvidence("prehighlight", 1.0, bestLeft, bestRight, 0, height,
            new Rectangle(bestLeft, 0, bestRight - bestLeft, height));
    }

    internal static string SelectText(OcrReadResult ocr, HighlightEvidence highlight)
    {
        if (ocr.Hits.Count == 0) return "";
        if (highlight.HasSelectedSpan)
        {
            var selected = ocr.Hits.Where(hit =>
                (hit.Left + hit.Right) / 2 >= highlight.Left!.Value - 4 &&
                (hit.Left + hit.Right) / 2 <= highlight.Right!.Value + 4).ToList();
            if (selected.Count > 0)
                return string.Concat(selected.Select(hit => hit.Text));
        }
        return ocr.Text;
    }
}

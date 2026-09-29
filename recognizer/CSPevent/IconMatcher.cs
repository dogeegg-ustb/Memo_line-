using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CSPevent;

internal sealed record IconMatchResult(
    string? IconId,
    string? Label,
    string? CommandHint,
    double Score,
    double RunnerUpScore,
    string State,
    Rectangle? ButtonRect,
    Point? HighlightCenter)
{
    internal bool IsMatched => State == "matched" && IconId is not null;
    internal bool IsCandidate => State == "candidate" && IconId is not null;

    internal static readonly IconMatchResult NoMatch = new(
        null, null, null, 0, 0, "no_match", null, null);
}

internal static class IconMatcher
{
    internal static IconMatchResult Match(
        IconCatalog catalog,
        Bitmap before,
        Bitmap? after,
        int clickX,
        int clickY,
        Highlight2DEvidence highlight,
        string language)
    {
        if (catalog.Templates.Count == 0 || before.Width < 8 || before.Height < 8)
            return IconMatchResult.NoMatch;

        // 1. 高光选区确认与图标字形质心精细对齐
        Rectangle buttonRect = DetermineButtonRegion(before, highlight, clickX, clickY);
        if (buttonRect.Width < 8 || buttonRect.Height < 8)
            return IconMatchResult.NoMatch;

        Point center = highlight.Center ?? new Point(
            buttonRect.Left + buttonRect.Width / 2,
            buttonRect.Top + buttonRect.Height / 2);

        // 2. 提取待匹配切片并进行多尺度归一化
        using Bitmap crop = CropRegion(before, buttonRect);
        var (normalizedGray, totalEnergy) = NormalizeToGrid(crop, 16);

        // 能量门禁：若前景特征点总能量过低（点击空白画布或面板纯底色），绝不误匹配
        if (totalEnergy < 4.0)
            return IconMatchResult.NoMatch;

        // 3. 计算与所有模板的匹配得分（具备 ±2px 微平移容错搜索）
        var scored = new List<(IconTemplate Icon, double Score)>();
        foreach (IconTemplate template in catalog.Templates)
        {
            double score = ComputeMatchScore(normalizedGray, template);
            scored.Add((template, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (scored.Count == 0) return IconMatchResult.NoMatch;

        var top = scored[0];
        double topScore = top.Score;
        double runnerUp = scored.Count > 1 ? scored[1].Score : 0.0;

        // A similar silhouette is insufficient to name a command. Require separation
        // from the next candidate even when the absolute score is high.
        string state;
        if (topScore >= 0.78 && topScore - runnerUp >= 0.12)
        {
            state = "matched";
        }
        else if (topScore >= 0.64 && topScore - runnerUp >= 0.08)
        {
            state = "candidate";
        }
        else
        {
            return IconMatchResult.NoMatch;
        }

        string localizedName = top.Icon.GetLocalizedName(language);
        return new IconMatchResult(
            top.Icon.IconId,
            localizedName,
            top.Icon.CommandHint,
            Math.Round(topScore, 3),
            Math.Round(runnerUp, 3),
            state,
            buttonRect,
            center);
    }

    internal static Rectangle DetermineButtonRegion(
        Bitmap image, Highlight2DEvidence highlight, int clickX, int clickY)
    {
        int imgW = image.Width;
        int imgH = image.Height;

        // 若已有可靠的 2D 高光边界
        if (highlight.Has2DBounds)
        {
            Rectangle b = highlight.Bounds!.Value;

            // 当高光区域为典型工具栏宽块（如 61x44 或更大）时，
            // 扫描内部非底色像素的加权质心，将切片框精准对齐到图标图形本身
            if (b.Width > 32 || b.Height > 32)
            {
                var glyphCentroid = FindGlyphCentroid(image, b);
                int anchorX = glyphCentroid?.X ?? (highlight.Center?.X ?? clickX);
                int anchorY = glyphCentroid?.Y ?? (highlight.Center?.Y ?? clickY);

                const int targetSize = 26;
                int left = Math.Clamp(anchorX - targetSize / 2, 0, Math.Max(0, imgW - targetSize));
                int top = Math.Clamp(anchorY - targetSize / 2, 0, Math.Max(0, imgH - targetSize));
                return new Rectangle(left, top, Math.Min(targetSize, imgW - left), Math.Min(targetSize, imgH - top));
            }

            // 紧凑高光块直接作为切片边界，适当填充至标准尺寸
            if (b.Width < 22 || b.Height < 22)
            {
                int cx = b.Left + b.Width / 2;
                int cy = b.Top + b.Height / 2;
                int size = Math.Max(24, Math.Max(b.Width, b.Height));
                int left = Math.Clamp(cx - size / 2, 0, Math.Max(0, imgW - size));
                int top = Math.Clamp(cy - size / 2, 0, Math.Max(0, imgH - size));
                return new Rectangle(left, top, Math.Min(size, imgW - left), Math.Min(size, imgH - top));
            }

            return b;
        }

        // 无 2D 边界时，以点击点为中心扩展 26x26 标准按钮区域
        const int defSize = 26;
        int defLeft = Math.Clamp(clickX - defSize / 2, 0, Math.Max(0, imgW - defSize));
        int defTop = Math.Clamp(clickY - defSize / 2, 0, Math.Max(0, imgH - defSize));
        return new Rectangle(defLeft, defTop, Math.Min(defSize, imgW - defLeft), Math.Min(defSize, imgH - defTop));
    }

    private static Point? FindGlyphCentroid(Bitmap image, Rectangle bounds)
    {
        // 采样边框四周像素以估计按钮背景亮度
        var borderLums = new List<double>();
        for (int x = bounds.Left; x < bounds.Right; x++)
        {
            Color c1 = image.GetPixel(x, bounds.Top);
            Color c2 = image.GetPixel(x, bounds.Bottom - 1);
            borderLums.Add(0.299 * c1.R + 0.587 * c1.G + 0.114 * c1.B);
            borderLums.Add(0.299 * c2.R + 0.587 * c2.G + 0.114 * c2.B);
        }
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            Color c1 = image.GetPixel(bounds.Left, y);
            Color c2 = image.GetPixel(bounds.Right - 1, y);
            borderLums.Add(0.299 * c1.R + 0.587 * c1.G + 0.114 * c1.B);
            borderLums.Add(0.299 * c2.R + 0.587 * c2.G + 0.114 * c2.B);
        }

        if (borderLums.Count == 0) return null;
        borderLums.Sort();
        double bgLum = borderLums[borderLums.Count / 2];

        double sumX = 0;
        double sumY = 0;
        double sumW = 0;

        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                Color c = image.GetPixel(x, y);
                double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                double delta = Math.Abs(lum - bgLum);
                if (delta > 24)
                {
                    sumX += x * delta;
                    sumY += y * delta;
                    sumW += delta;
                }
            }
        }

        if (sumW < 200) return null;
        return new Point((int)Math.Round(sumX / sumW), (int)Math.Round(sumY / sumW));
    }

    internal static Bitmap CropRegion(Bitmap source, Rectangle rect)
    {
        rect.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
            return new Bitmap(1, 1);
        return source.Clone(rect, PixelFormat.Format32bppArgb);
    }

    internal static (double[,] Foreground, double TotalEnergy) NormalizeToGrid(Bitmap source, int gridSize)
    {
        using var resized = new Bitmap(gridSize, gridSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, gridSize, gridSize));
        }

        var gray = new double[gridSize, gridSize];
        var borderSamples = new List<double>();

        for (int y = 0; y < gridSize; y++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                Color c = resized.GetPixel(x, y);
                double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                gray[y, x] = lum;
                if (x == 0 || x == gridSize - 1 || y == 0 || y == gridSize - 1)
                    borderSamples.Add(lum);
            }
        }

        borderSamples.Sort();
        double bg = borderSamples[borderSamples.Count / 2];

        var foreground = new double[gridSize, gridSize];
        double totalEnergy = 0;
        for (int y = 0; y < gridSize; y++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                double delta = Math.Abs(gray[y, x] - bg);
                double val = delta > 24 ? Math.Clamp(delta / 70.0, 0.0, 1.0) : 0.0;
                foreground[y, x] = val;
                totalEnergy += val;
            }
        }

        return (foreground, totalEnergy);
    }

    /// <summary>
    /// 具备多偏移 (±2px) 微平移窗口搜索的模板匹配算法。
    /// 在微秒级别内寻找最佳对齐位移，大幅提升小图标与高DPI界面下的识别成功率。
    /// </summary>
    internal static double ComputeMatchScore(double[,] observed, IconTemplate template)
    {
        int size = template.GridSize;
        bool[,] mask = template.Mask;
        double bestF1 = 0;

        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                double matchFore = 0;
                double totalFore = 0;
                double penaltyBack = 0;

                for (int y = 0; y < size; y++)
                {
                    int sy = y - dy;
                    for (int x = 0; x < size; x++)
                    {
                        int sx = x - dx;
                        double val = (sx >= 0 && sx < size && sy >= 0 && sy < size)
                            ? observed[sy, sx]
                            : 0.0;

                        if (mask[y, x])
                        {
                            matchFore += val;
                            totalFore += 1.0;
                        }
                        else
                        {
                            penaltyBack += val;
                        }
                    }
                }

                if (totalFore > 0)
                {
                    double precision = matchFore / (matchFore + penaltyBack + 1e-5);
                    double recall = matchFore / (totalFore + 1e-5);
                    if (precision + recall > 1e-5)
                    {
                        double f1 = 2.0 * precision * recall / (precision + recall);
                        if (f1 > bestF1) bestF1 = f1;
                    }
                }
            }
        }

        return Math.Clamp(bestF1, 0.0, 1.0);
    }
}

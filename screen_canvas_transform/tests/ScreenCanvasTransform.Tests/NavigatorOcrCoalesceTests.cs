using ScreenCanvasTransform.Ocr;
using Xunit;

namespace ScreenCanvasTransform.Tests;

public sealed class NavigatorOcrCoalesceTests
{
    [Fact]
    public void Coalesce_ZhHansSplitDecimal_JoinsToAscii()
    {
        // Captured from Windows zh-Hans OCR on CSP "14.8" / "0.0" (upscaled pixel boxes).
        var tokens = new (string, double, double, double, double, double)[]
        {
            ("14", 216, 700, 653, 747, 75),
            ("，", 246, 764, 756, 772, 16),
            ("8", 216, 830, 807, 853, 76),
            ("0", 432, 231, 208, 254, 76),
            ("．", 461, 272, 264, 281, 15),
            ("0", 432, 339, 316, 362, 76),
        };

        var hits = NavigatorOcrService.CoalesceOcrNumberTokens(tokens);
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Text == "14.8");
        Assert.Contains(hits, h => h.Text == "0.0");
    }

    [Fact]
    public void Coalesce_AmpersandAsEightDot_JoinsEighteenPointNine()
    {
        // Real Windows OCR on CSP "18.9": "1" + "&" + "9"  (& ≈ "8.")
        var tokens = new (string, double, double, double, double, double)[]
        {
            ("1", 198, 649, 632, 666, 72),
            ("&", 195, 715, 679, 751, 75),
            ("9", 195, 809, 787, 832, 75),
            ("0", 411, 210, 187, 233, 75),
            ("，", 441, 251, 243, 260, 15),
            ("0", 411, 318, 296, 341, 75),
        };

        var hits = NavigatorOcrService.CoalesceOcrNumberTokens(tokens);
        Assert.Contains(hits, h => h.Text == "18.9");
        Assert.Contains(hits, h => h.Text == "0.0");
        Assert.True(NavigatorOcrService.TryParseScale("18.9", out float scale));
        Assert.Equal(18.9f, scale, precision: 3);
    }

    [Fact]
    public void TryParseScale_AcceptsCoalescedDecimal()
    {
        Assert.True(NavigatorOcrService.TryParseScale("14.8", out float v));
        Assert.Equal(14.8f, v, precision: 3);
    }

    [Fact]
    public void TryParseScale_AcceptsChineseDecimalPunctuationInToken()
    {
        Assert.True(NavigatorOcrService.TryParseScale("14，8", out float v));
        Assert.Equal(14.8f, v, precision: 3);
    }

    [Fact]
    public void NormalizeDecimalPunctuation_MapsFullwidthAndStripsJunk()
    {
        Assert.Equal("14.8", NavigatorOcrService.NormalizeDecimalPunctuation("14，8"));
        Assert.Equal("0.0", NavigatorOcrService.NormalizeDecimalPunctuation("0．0"));
        Assert.Equal("18.9", NavigatorOcrService.NormalizeDecimalPunctuation("18.9%"));
    }
}

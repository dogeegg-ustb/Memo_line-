using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Ui;

namespace ScreenCanvasTransform.Services;

/// <summary>
/// 流水线末端：用 OCR 显示角做逆向旋转（屏侧几何 → 0° 语义 L/T/R/B），输出视口对应标签。
/// </summary>
public static class ViewportCorrespondenceMapper
{
    private static readonly int[] EdgeOrder =
    {
        WorkspaceEdgeBits.Left,
        WorkspaceEdgeBits.Top,
        WorkspaceEdgeBits.Right,
        WorkspaceEdgeBits.Bottom
    };

    public static int NearestQuarter(float degrees)
    {
        double n = degrees % 360.0;
        if (n < 0) n += 360.0;
        int q = (int)Math.Round(n / 90.0) % 4;
        return q < 0 ? q + 4 : q;
    }

    private static int BitIndex(int bit) => bit switch
    {
        WorkspaceEdgeBits.Left => 0,
        WorkspaceEdgeBits.Top => 1,
        WorkspaceEdgeBits.Right => 2,
        WorkspaceEdgeBits.Bottom => 3,
        _ => -1
    };

    /// <summary>显示顺时针 q×90° 时，屏侧 S 上的画布语义边。</summary>
    public static int CanonicalRoleFromScreenSide(int screenSideBit, int quartersCw)
    {
        int idx = BitIndex(screenSideBit);
        if (idx < 0) return 0;
        int q = ((quartersCw % 4) + 4) % 4;
        return EdgeOrder[(idx - q + 4) % 4];
    }

    public static int InferScreenSide(double midX, double midY, double centerX, double centerY, bool horizontal)
    {
        if (horizontal)
            return midY < centerY ? WorkspaceEdgeBits.Top : WorkspaceEdgeBits.Bottom;
        return midX < centerX ? WorkspaceEdgeBits.Left : WorkspaceEdgeBits.Right;
    }

    public static CompleteEdgeOverlayWindow.LabeledScreenEdge MapScreenEdge(
        double screenX0, double screenY0, double screenX1, double screenY1,
        bool isComplete, float ocrRotationDegrees, float ocrRotationConfidence,
        double canvasCenterScreenX, double canvasCenterScreenY)
    {
        double mx = 0.5 * (screenX0 + screenX1);
        double my = 0.5 * (screenY0 + screenY1);
        bool horizontal = Math.Abs(screenY1 - screenY0) <= Math.Abs(screenX1 - screenX0);
        int screenSide = InferScreenSide(mx, my, canvasCenterScreenX, canvasCenterScreenY, horizontal);
        int workspaceEdge = ocrRotationConfidence >= 0.2f
            ? CanonicalRoleFromScreenSide(screenSide, NearestQuarter(ocrRotationDegrees))
            : screenSide;
        return new CompleteEdgeOverlayWindow.LabeledScreenEdge(
            screenX0, screenY0, screenX1, screenY1, workspaceEdge, isComplete);
    }

    public static IReadOnlyList<CompleteEdgeOverlayWindow.LabeledScreenEdge> MapObservedEdges(
        TransformSnapshotDto snapshot, CaptureSession session)
    {
        if (snapshot.ObservedRedEdges.Length == 0)
            return Array.Empty<CompleteEdgeOverlayWindow.LabeledScreenEdge>();

        var nav = snapshot.NavigatorCanvas;
        IntRect bounds = !nav.BoundsScreen.IsEmpty ? nav.BoundsScreen : default;
        if (bounds.IsEmpty && !nav.BoundsCapture.IsEmpty)
        {
            bounds = new IntRect(
                nav.BoundsCapture.Left + session.OriginX,
                nav.BoundsCapture.Top + session.OriginY,
                nav.BoundsCapture.Width,
                nav.BoundsCapture.Height);
        }
        double cx = bounds.IsEmpty
            ? snapshot.NavigatorThumbnailRoi.Left + session.OriginX + snapshot.NavigatorThumbnailRoi.Width * 0.5
            : bounds.Left + bounds.Width * 0.5;
        double cy = bounds.IsEmpty
            ? snapshot.NavigatorThumbnailRoi.Top + session.OriginY + snapshot.NavigatorThumbnailRoi.Height * 0.5
            : bounds.Top + bounds.Height * 0.5;

        int ox = session.OriginX;
        int oy = session.OriginY;
        float rot = snapshot.Numbers.RotationDegrees;
        float rotConf = snapshot.Numbers.RotationConfidence;

        var list = new List<CompleteEdgeOverlayWindow.LabeledScreenEdge>(snapshot.ObservedRedEdges.Length);
        foreach (var e in snapshot.ObservedRedEdges)
        {
            list.Add(MapScreenEdge(
                e.P0CaptureX + ox, e.P0CaptureY + oy,
                e.P1CaptureX + ox, e.P1CaptureY + oy,
                e.IsComplete, rot, rotConf, cx, cy));
        }
        return list;
    }

    public static string FormatCorrespondenceLog(
        IReadOnlyList<CompleteEdgeOverlayWindow.LabeledScreenEdge> edges,
        float ocrRotationDegrees, float ocrRotationConfidence)
    {
        if (edges.Count == 0) return "视口对应：无观测边";
        int q = ocrRotationConfidence >= 0.2f ? NearestQuarter(ocrRotationDegrees) : -1;
        var parts = new List<string>(edges.Count);
        foreach (var e in edges)
        {
            string? label = WorkspaceEdgeBits.ToLabel(e.WorkspaceEdge);
            double mx = 0.5 * (e.X0 + e.X1);
            double my = 0.5 * (e.Y0 + e.Y1);
            parts.Add($"{label ?? "?"}@({mx:F0},{my:F0})");
        }
        string rotNote = q >= 0 ? $"逆旋q={q} OCR={ocrRotationDegrees:F1}°" : "OCR角不可用，用屏侧几何";
        return $"视口对应[{rotNote}]：{string.Join("，", parts)}";
    }
}

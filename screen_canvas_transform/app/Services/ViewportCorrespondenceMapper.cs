using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Interop;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Ui;

namespace ScreenCanvasTransform.Services;

/// <summary>
/// 视口边标签：优先使用 native 已指派的 workspace_edge（系统对每条边的语义认知）。
/// MUST NOT 在流水线末端再用 OCR 角给文字单独「转圈」覆盖边状态。
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

    public static bool IsSingleEdgeRole(int workspaceEdge) =>
        workspaceEdge == WorkspaceEdgeBits.Left
        || workspaceEdge == WorkspaceEdgeBits.Top
        || workspaceEdge == WorkspaceEdgeBits.Right
        || workspaceEdge == WorkspaceEdgeBits.Bottom;

    /// <summary>显示顺时针 q×90° 时，屏侧 S 上的画布语义边（仅无无边角色时的回退）。</summary>
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

    /// <summary>
    /// 有系统边角色则直接用；否则用几何旋转角（非 OCR）把屏侧映到语义边。
    /// </summary>
    public static CompleteEdgeOverlayWindow.LabeledScreenEdge MapScreenEdge(
        double screenX0, double screenY0, double screenX1, double screenY1,
        bool isComplete, int assignedWorkspaceEdge,
        float geometryRotationDegrees, float geometryRotationConfidence,
        double canvasCenterScreenX, double canvasCenterScreenY)
    {
        int workspaceEdge = assignedWorkspaceEdge;
        if (!IsSingleEdgeRole(workspaceEdge))
        {
            double mx = 0.5 * (screenX0 + screenX1);
            double my = 0.5 * (screenY0 + screenY1);
            bool horizontal = Math.Abs(screenY1 - screenY0) <= Math.Abs(screenX1 - screenX0);
            int screenSide = InferScreenSide(mx, my, canvasCenterScreenX, canvasCenterScreenY, horizontal);
            workspaceEdge = geometryRotationConfidence >= 0.2f
                ? CanonicalRoleFromScreenSide(screenSide, NearestQuarter(geometryRotationDegrees))
                : screenSide;
        }

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
        // 回退角用几何权威，禁止 OCR 读数驱动标签
        float rot = snapshot.RotationDegreesGeometry;
        float rotConf = float.IsFinite(rot) ? 1f : 0f;

        var list = new List<CompleteEdgeOverlayWindow.LabeledScreenEdge>(snapshot.ObservedRedEdges.Length);
        foreach (var e in snapshot.ObservedRedEdges)
        {
            list.Add(MapScreenEdge(
                e.P0CaptureX + ox, e.P0CaptureY + oy,
                e.P1CaptureX + ox, e.P1CaptureY + oy,
                e.IsComplete, e.WorkspaceEdge, rot, rotConf, cx, cy));
        }
        return list;
    }

    /// <summary>
    /// Maps the completed viewport frame, rather than only its observed red fragments,
    /// into physical screen coordinates. The semantic corners are emitted in the same
    /// order as the navigator's viewport frame: TL → TR → BR → BL.
    /// </summary>
    public static IReadOnlyList<CompleteEdgeOverlayWindow.LabeledScreenEdge> MapCompletedViewportEdges(
        NativeSct.SctViewportFrame viewport, int captureOriginX, int captureOriginY)
    {
        var corners = new[]
        {
            viewport.Corner0,
            viewport.Corner1,
            viewport.Corner2,
            viewport.Corner3
        };
        if (corners.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y)))
            return Array.Empty<CompleteEdgeOverlayWindow.LabeledScreenEdge>();

        // An empty native frame is the direct-workspace path, or an unsuccessful
        // completion. Do not draw a degenerate origin-only rectangle in either case.
        if (viewport.Width <= 2f || viewport.Height <= 2f)
            return Array.Empty<CompleteEdgeOverlayWindow.LabeledScreenEdge>();

        int[] roles =
        {
            WorkspaceEdgeBits.Top,
            WorkspaceEdgeBits.Right,
            WorkspaceEdgeBits.Bottom,
            WorkspaceEdgeBits.Left
        };
        var edges = new CompleteEdgeOverlayWindow.LabeledScreenEdge[4];
        for (int i = 0; i < edges.Length; i++)
        {
            var p0 = corners[i];
            var p1 = corners[(i + 1) % corners.Length];
            edges[i] = new CompleteEdgeOverlayWindow.LabeledScreenEdge(
                p0.X + captureOriginX, p0.Y + captureOriginY,
                p1.X + captureOriginX, p1.Y + captureOriginY,
                roles[i], IsComplete: true);
        }
        return edges;
    }

    public static string FormatCorrespondenceLog(
        IReadOnlyList<CompleteEdgeOverlayWindow.LabeledScreenEdge> edges,
        int assignedRoleCount, float geometryRotationDegrees)
    {
        if (edges.Count == 0) return "视口对应：无观测边";
        var parts = new List<string>(edges.Count);
        foreach (var e in edges)
        {
            string? label = WorkspaceEdgeBits.ToLabel(e.WorkspaceEdge);
            double mx = 0.5 * (e.X0 + e.X1);
            double my = 0.5 * (e.Y0 + e.Y1);
            parts.Add($"{label ?? "?"}@({mx:F0},{my:F0})");
        }
        string src = assignedRoleCount > 0
            ? $"边状态×{assignedRoleCount}"
            : $"几何回退 geo={geometryRotationDegrees:F1}°";
        return $"视口对应[{src}]：{string.Join("，", parts)}";
    }
}

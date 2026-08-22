using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Diagnostics;
using ScreenCanvasTransform.Interop;
using ScreenCanvasTransform.Models;

namespace ScreenCanvasTransform.Detection;

public sealed class DetectOutcome
{
    public bool Success { get; init; }
    public int Status { get; init; }
    public string StatusName { get; init; } = "";
    public string Message { get; init; } = "";
    public string SourceCaptureId { get; init; } = "";
    public string SourceRevision { get; init; } = "";
    public IntRect RectCapturePx { get; init; }
    public IntRect RectScreenPhysicalPx { get; init; }
    public int EvidenceGrade { get; init; }
    public float Confidence { get; init; }
    public WorkspaceBackgroundModel? Background { get; init; }
    public int SourceBackend { get; init; }
}

/// <summary>
/// Native entry for SCT. Workspace border detection is compiled-in C++
/// (WorkspaceBorderDetector sources), not a separate DLL/API.
/// </summary>
public sealed class SctNativeService
{
    public DetectOutcome DetectWorkspace(CaptureSession session)
    {
        if (session.WorkspaceUserRoiCapturePx is null)
        {
            return FailDetect(-1, "InvalidInput", "尚未确认工作区用户 ROI。", session.CaptureId);
        }

        var userRoi = session.WorkspaceUserRoiCapturePx.Value;
        LiveDebugLog.Write(
            $"[DetectWorkspace] capture={session.CaptureId} frame={session.FrozenCapture.Width}x{session.FrozenCapture.Height} " +
            $"origin=({session.OriginX},{session.OriginY}) dpi=({session.DpiX:F1},{session.DpiY:F1}) " +
            $"userRoi={userRoi}");

        var outcome = WithLockedFrame(session, (scan0, stride) =>
        {
            IntPtr captureIdPtr = NativeSct.StringToHGlobalAnsi(session.CaptureId);
            try
            {
                var request = new NativeSct.SctDetectRequest
                {
                    Bgra = scan0,
                    Width = session.FrozenCapture.Width,
                    Height = session.FrozenCapture.Height,
                    Stride = stride,
                    UserRoi = NativeSct.SctIntRect.From(userRoi),
                    DpiX = session.DpiX,
                    DpiY = session.DpiY,
                    OriginX = session.OriginX,
                    OriginY = session.OriginY,
                    CaptureId = captureIdPtr
                };

                var result = new NativeSct.SctDetectResult
                {
                    Message = "",
                    SourceCaptureId = "",
                    SourceRevision = ""
                };

                int rc = NativeSct.sct_detect_workspace(in request, ref result);
                return MapDetect(session, rc, result, requireBackground: true);
            }
            finally
            {
                Marshal.FreeHGlobal(captureIdPtr);
            }
        });

        LiveDebugLog.Write(
            $"[DetectWorkspace] result ok={outcome.Success} status={outcome.Status} ({outcome.StatusName}) " +
            $"msg={outcome.Message} grade={outcome.EvidenceGrade} conf={outcome.Confidence:F3} " +
            $"rectCap={outcome.RectCapturePx} rectScr={outcome.RectScreenPhysicalPx} " +
            $"hasBg={(outcome.Background is not null)} rev={outcome.SourceRevision}");
        if (outcome.Background is not null)
        {
            var bg = outcome.Background;
            LiveDebugLog.Write(
                $"[DetectWorkspace] bg Lab=({bg.CenterLabL:F1},{bg.CenterLabA:F1},{bg.CenterLabB:F1}) " +
                $"dE=({bg.StrongDeltaE:F1}/{bg.WeakDeltaE:F1}) conf={bg.Confidence:F2}");
        }

        SaveDebugCapture(session, userRoi, outcome);
        return outcome;
    }

    public DetectOutcome DetectNavigatorThumbnailCii(
        CaptureSession session,
        IntRect navigatorRoiCapturePx,
        WorkspaceBackgroundModel background)
    {
        return WithLockedFrame(session, (scan0, stride) =>
        {
            IntPtr captureIdPtr = NativeSct.StringToHGlobalAnsi(session.CaptureId);
            try
            {
                var request = new NativeSct.SctCiiRequest
                {
                    Bgra = scan0,
                    Width = session.FrozenCapture.Width,
                    Height = session.FrozenCapture.Height,
                    Stride = stride,
                    NavigatorRoi = NativeSct.SctIntRect.From(navigatorRoiCapturePx),
                    DpiX = session.DpiX,
                    DpiY = session.DpiY,
                    OriginX = session.OriginX,
                    OriginY = session.OriginY,
                    CaptureId = captureIdPtr,
                    Background = background.ToNative()
                };

                var result = new NativeSct.SctDetectResult
                {
                    Message = "",
                    SourceCaptureId = "",
                    SourceRevision = ""
                };

                int rc = NativeSct.sct_detect_navigator_thumbnail_cii(in request, ref result);
                return MapDetect(session, rc, result, requireBackground: false);
            }
            finally
            {
                Marshal.FreeHGlobal(captureIdPtr);
            }
        });
    }

    public CanvasObservationDto ObserveCanvas(
        CaptureSession session,
        IntRect roiCapturePx,
        WorkspaceBackgroundModel background)
    {
        return WithLockedFrame(session, (scan0, stride) =>
        {
            var request = new NativeSct.SctCanvasObserveRequest
            {
                Bgra = scan0,
                Width = session.FrozenCapture.Width,
                Height = session.FrozenCapture.Height,
                Stride = stride,
                RoiCapture = NativeSct.SctIntRect.From(roiCapturePx),
                OriginX = session.OriginX,
                OriginY = session.OriginY,
                Background = background.ToNative(),
                DpiScale = Math.Max(session.DpiX, session.DpiY) / 96f
            };

            var result = new NativeSct.SctCanvasObservation { AmbiguityReason = "" };
            _ = NativeSct.sct_observe_canvas(in request, ref result);
            return CanvasObservationDto.FromNative(result);
        });
    }

    public NativeSct.SctViewportFrame CompleteViewportFrame(
        CaptureSession session,
        IntRect thumbnailRoiCapturePx,
        IntRect navigatorCanvasBoundsCapturePx,
        float workspaceAspect)
    {
        return WithLockedFrame(session, (scan0, stride) =>
        {
            var request = new NativeSct.SctViewportRequest
            {
                Bgra = scan0,
                Width = session.FrozenCapture.Width,
                Height = session.FrozenCapture.Height,
                Stride = stride,
                ThumbnailRoi = NativeSct.SctIntRect.From(thumbnailRoiCapturePx),
                NavigatorCanvasBounds = NativeSct.SctIntRect.From(navigatorCanvasBoundsCapturePx),
                WorkspaceAspect = workspaceAspect,
                DpiScale = Math.Max(session.DpiX, session.DpiY) / 96f
            };

            var result = new NativeSct.SctViewportFrame { Message = "" };
            _ = NativeSct.sct_complete_viewport_frame(in request, ref result);
            return result;
        });
    }

    public TransformSnapshotDto SolveTransform(NativeSct.SctSolveRequest request)
    {
        var result = new NativeSct.SctTransformSnapshot
        {
            SnapshotId = "",
            CaptureId = "",
            SourceRevision = "",
            Failure = new NativeSct.SctFailure
            {
                Message = "",
                CaptureId = "",
                SourceRevision = "",
                EvidenceSummary = ""
            },
            Numbers = request.Numbers,
            WorkspaceCanvas = request.WorkspaceCanvas,
            NavigatorCanvas = request.NavigatorCanvas,
            Viewport = request.Viewport
        };
        _ = NativeSct.sct_solve_transform(in request, ref result);
        return TransformSnapshotDto.FromNative(result);
    }

    private static DetectOutcome MapDetect(
        CaptureSession session,
        int rc,
        NativeSct.SctDetectResult result,
        bool requireBackground)
    {
        string sourceId = result.SourceCaptureId ?? "";
        bool idMatch = string.Equals(sourceId, session.CaptureId, StringComparison.Ordinal);
        bool ok = rc == NativeSct.StatusOk && result.Status == NativeSct.StatusOk && idMatch;
        if (requireBackground && result.HasBackground == 0)
            ok = false;

        string message = string.IsNullOrWhiteSpace(result.Message)
            ? (ok ? "检测成功。" : "检测失败。")
            : result.Message;

        if (result.Status == NativeSct.StatusOk && !idMatch)
            message = $"CaptureId 不匹配（会话 {session.CaptureId}，结果 {sourceId}），已丢弃。";

        WorkspaceBackgroundModel? bg = null;
        if (result.HasBackground != 0)
        {
            bg = WorkspaceBackgroundModel.FromNative(
                result.Background,
                sourceId,
                result.SourceRevision ?? "");
        }

        return new DetectOutcome
        {
            Success = ok,
            Status = result.Status != 0 ? result.Status : rc,
            StatusName = NativeSct.GetStatusName(result.Status != 0 ? result.Status : rc),
            Message = message,
            SourceCaptureId = sourceId,
            SourceRevision = result.SourceRevision ?? "",
            RectCapturePx = result.WorkspaceCapture.ToIntRect(),
            RectScreenPhysicalPx = result.WorkspaceScreen.ToIntRect(),
            EvidenceGrade = result.EvidenceGrade,
            Confidence = result.Confidence,
            Background = bg,
            SourceBackend = result.SourceBackend
        };
    }

    private static void SaveDebugCapture(CaptureSession session, IntRect userRoi, DetectOutcome outcome)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "sct_live_debug", session.CaptureId);
            Directory.CreateDirectory(dir);
            string tag = outcome.Success ? "ok" : $"fail_{outcome.StatusName}";
            string png = Path.Combine(dir, $"{tag}_capture.png");
            session.FrozenCapture.Save(png, ImageFormat.Png);
            File.WriteAllText(
                Path.Combine(dir, $"{tag}_meta.txt"),
                $"captureId={session.CaptureId}\n" +
                $"frame={session.FrozenCapture.Width}x{session.FrozenCapture.Height}\n" +
                $"origin=({session.OriginX},{session.OriginY})\n" +
                $"dpi=({session.DpiX},{session.DpiY})\n" +
                $"userRoi={userRoi}\n" +
                $"ok={outcome.Success}\n" +
                $"status={outcome.Status} {outcome.StatusName}\n" +
                $"message={outcome.Message}\n" +
                $"grade={outcome.EvidenceGrade}\n" +
                $"conf={outcome.Confidence}\n" +
                $"rectCap={outcome.RectCapturePx}\n" +
                $"rectScr={outcome.RectScreenPhysicalPx}\n");
            LiveDebugLog.Write($"[DetectWorkspace] 已保存调试帧: {dir}");
        }
        catch (Exception ex)
        {
            LiveDebugLog.Write($"[DetectWorkspace] 保存调试帧失败: {ex.Message}");
        }
    }

    private static DetectOutcome FailDetect(int status, string name, string message, string captureId)
        => new()
        {
            Success = false,
            Status = status,
            StatusName = name,
            Message = message,
            SourceCaptureId = captureId
        };

    private static T WithLockedFrame<T>(CaptureSession session, Func<IntPtr, int, T> work)
    {
        var bmp = session.FrozenCapture;
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            return work(data.Scan0, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}

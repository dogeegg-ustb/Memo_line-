using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WorkspaceBorderDetect.Capture;
using WorkspaceBorderDetect.Interop;
using WorkspaceBorderDetect.Models;

namespace WorkspaceBorderDetect.Detection;

public sealed class DetectionOutcome
{
    public bool Success { get; init; }
    public int Status { get; init; }
    public string StatusName { get; init; } = "";
    public string Message { get; init; } = "";
    public string SourceCaptureId { get; init; } = "";
    public IntRect WorkspaceCapturePx { get; init; }
    public IntRect WorkspaceScreenPhysicalPx { get; init; }
    public int EvidenceGrade { get; init; }
    public float Confidence { get; init; }
}

/// <summary>
/// Calls the native detector against a frozen capture session.
/// </summary>
public sealed class WorkspaceDetectorService
{
    public DetectionOutcome Detect(CaptureSession session)
    {
        if (session.UserRoiCapturePx is null)
        {
            return new DetectionOutcome
            {
                Success = false,
                Status = -1,
                StatusName = "InvalidInput",
                Message = "尚未确认用户 ROI。",
                SourceCaptureId = session.CaptureId
            };
        }

        var roi = session.UserRoiCapturePx.Value;
        var bmp = session.FrozenCapture;

        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        IntPtr captureIdPtr = IntPtr.Zero;
        try
        {
            captureIdPtr = NativeDetector.StringToHGlobalAnsi(session.CaptureId);

            var request = new NativeDetector.WbDetectRequest
            {
                Bgra = data.Scan0,
                Width = bmp.Width,
                Height = bmp.Height,
                Stride = data.Stride,
                UserRoi = NativeDetector.WbIntRect.From(roi),
                DpiX = session.DpiX,
                DpiY = session.DpiY,
                OriginX = session.OriginX,
                OriginY = session.OriginY,
                CaptureId = captureIdPtr
            };

            var result = new NativeDetector.WbDetectResult
            {
                Message = string.Empty,
                SourceCaptureId = string.Empty
            };

            int rc;
            try
            {
                rc = NativeDetector.wb_detect(in request, ref result);
            }
            catch (DllNotFoundException)
            {
                return new DetectionOutcome
                {
                    Success = false,
                    Status = -2,
                    StatusName = "DllNotFound",
                    Message = $"未找到 {NativeDetector.DllName}。请先构建并复制原生 DLL。",
                    SourceCaptureId = session.CaptureId
                };
            }
            catch (EntryPointNotFoundException ex)
            {
                return new DetectionOutcome
                {
                    Success = false,
                    Status = -3,
                    StatusName = "EntryPointNotFound",
                    Message = $"原生导出缺失: {ex.Message}",
                    SourceCaptureId = session.CaptureId
                };
            }

            string sourceId = result.SourceCaptureId ?? string.Empty;
            bool idMatch = string.Equals(sourceId, session.CaptureId, StringComparison.Ordinal);
            bool ok = rc == NativeDetector.StatusOk && result.Status == NativeDetector.StatusOk && idMatch;

            string message = string.IsNullOrWhiteSpace(result.Message)
                ? (ok ? "检测成功。" : "检测失败。")
                : result.Message;

            if (result.Status == NativeDetector.StatusOk && !idMatch)
            {
                message = $"CaptureId 不匹配（会话 {session.CaptureId}，结果 {sourceId}），已丢弃。";
            }

            return new DetectionOutcome
            {
                Success = ok,
                Status = result.Status != 0 ? result.Status : rc,
                StatusName = NativeDetector.GetStatusName(result.Status != 0 ? result.Status : rc),
                Message = message,
                SourceCaptureId = sourceId,
                WorkspaceCapturePx = result.WorkspaceCapture.ToIntRect(),
                WorkspaceScreenPhysicalPx = result.WorkspaceScreen.ToIntRect(),
                EvidenceGrade = result.EvidenceGrade,
                Confidence = result.Confidence
            };
        }
        finally
        {
            bmp.UnlockBits(data);
            if (captureIdPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(captureIdPtr);
        }
    }
}

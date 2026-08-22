using System.Runtime.InteropServices;
using System.Text;
using ScreenCanvasTransform.Models;

namespace ScreenCanvasTransform.Interop;

/// <summary>
/// P/Invoke surface for ScreenCanvasNative.dll (SCT_API_VERSION 1).
/// Host must not reimplement transform / viewport / C-II semantics.
/// </summary>
public static class NativeSct
{
    public const string DllName = "ScreenCanvasNative.dll";
    public const int ApiVersionExpected = 1;
    public const int StatusOk = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SctIntRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public IntRect ToIntRect() => new(Left, Top, Right, Bottom);

        public static SctIntRect From(IntRect r) => new()
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SctBackgroundModel
    {
        public float CenterLabL;
        public float CenterLabA;
        public float CenterLabB;
        public float StrongDeltaE;
        public float WeakDeltaE;
        public float Confidence;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SctDetectRequest
    {
        public IntPtr Bgra;
        public int Width;
        public int Height;
        public int Stride;
        public SctIntRect UserRoi;
        public float DpiX;
        public float DpiY;
        public int OriginX;
        public int OriginY;
        public IntPtr CaptureId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SctDetectResult
    {
        public int Status;
        public SctIntRect WorkspaceCapture;
        public SctIntRect WorkspaceScreen;
        public int EvidenceGrade;
        public float Confidence;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Message;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SourceCaptureId;

        public SctBackgroundModel Background;
        public int HasBackground;
        public int SourceBackend;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SourceRevision;

        public int ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SctCiiRequest
    {
        public IntPtr Bgra;
        public int Width;
        public int Height;
        public int Stride;
        public SctIntRect NavigatorRoi;
        public float DpiX;
        public float DpiY;
        public int OriginX;
        public int OriginY;
        public IntPtr CaptureId;
        public SctBackgroundModel Background;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SctCanvasObserveRequest
    {
        public IntPtr Bgra;
        public int Width;
        public int Height;
        public int Stride;
        public SctIntRect RoiCapture;
        public int OriginX;
        public int OriginY;
        public SctBackgroundModel Background;
        public float DpiScale;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SctCanvasObservation
    {
        public int Status;
        public SctIntRect BoundsCapture;
        public SctIntRect BoundsScreen;
        public float AspectRatio;
        public float Confidence;
        public int VisibleEdgesMask;
        public float BoundarySupport0;
        public float BoundarySupport1;
        public float BoundarySupport2;
        public float BoundarySupport3;
        public int FourSidesComplete;
        public int Ambiguous;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string AmbiguityReason;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SctViewportRequest
    {
        public IntPtr Bgra;
        public int Width;
        public int Height;
        public int Stride;
        public SctIntRect ThumbnailRoi;
        public SctIntRect NavigatorCanvasBounds;
        public float WorkspaceAspect;
        public float DpiScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SctVec2
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SctViewportFrame
    {
        public int Status;
        public SctVec2 OriginTopLeftDisplayed;
        public SctVec2 AxisXDisplayed;
        public SctVec2 AxisYDisplayed;
        public float Width;
        public float Height;
        public SctVec2 Corner0;
        public SctVec2 Corner1;
        public SctVec2 Corner2;
        public SctVec2 Corner3;
        public int VisibleEdgeCount;
        public int CompletionStrategy;
        public float Confidence;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Message;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SctNumericReading
    {
        public float ScalePercent;
        public float RotationDegrees;
        public float ScaleConfidence;
        public float RotationConfidence;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ScaleRaw;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string RotationRaw;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string CaptureId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SctAffine2D
    {
        public double M0, M1, M2, M3, M4, M5;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SctMarkerGeometry
    {
        public SctVec2 AnchorScreen;
        public SctVec2 XArmEndScreen;
        public SctVec2 YArmEndScreen;
        public int Offscreen;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SctSolveRequest
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string CaptureId;

        public ulong Generation;
        public SctIntRect WorkspaceRoiScreen;
        public SctIntRect NavigatorRoiScreen;
        public SctIntRect NavigatorThumbnailRoiScreen;
        public SctCanvasObservation WorkspaceCanvas;
        public SctCanvasObservation NavigatorCanvas;
        public SctNumericReading Numbers;
        public SctViewportFrame Viewport;
        public float PreviousScalePercent;
        public float InitialScalePercent;
        public double MarkerEpsilonCanvas;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SctFailure
    {
        public int Stage;
        public int Status;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Message;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string CaptureId;

        public ulong Generation;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SourceRevision;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string EvidenceSummary;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SctTransformSnapshot
    {
        public int Status;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SnapshotId;

        public ulong Generation;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string CaptureId;

        public SctIntRect WorkspaceRoi;
        public SctIntRect NavigatorRoi;
        public SctIntRect NavigatorThumbnailRoi;
        public SctCanvasObservation WorkspaceCanvas;
        public SctCanvasObservation NavigatorCanvas;
        public SctNumericReading Numbers;
        public SctViewportFrame Viewport;
        public float ScaleReference;
        public float RelativeScale;
        public float CumulativeRelativeScale;
        public float RotationDegrees;
        public SctAffine2D ScreenToWorkspace;
        public SctAffine2D WorkspaceToScreen;
        public SctAffine2D WorkspaceToCanvas;
        public SctAffine2D CanvasToWorkspace;
        public SctAffine2D ScreenToCanvas;
        public SctAffine2D CanvasToScreen;
        public SctMarkerGeometry Marker;
        public float Confidence;
        public int UsedDirectWorkspacePath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SourceRevision;

        public int CoordinateConventionVersion;
        public SctFailure Failure;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sct_api_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr sct_status_name(int status);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr sct_source_revision();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sct_detect_workspace(in SctDetectRequest req, ref SctDetectResult result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sct_detect_navigator_thumbnail_cii(in SctCiiRequest req, ref SctDetectResult result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sct_observe_canvas(in SctCanvasObserveRequest req, ref SctCanvasObservation result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sct_complete_viewport_frame(in SctViewportRequest req, ref SctViewportFrame result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sct_solve_transform(in SctSolveRequest req, ref SctTransformSnapshot result);

    public static string GetStatusName(int status)
    {
        try
        {
            IntPtr p = sct_status_name(status);
            if (p == IntPtr.Zero)
                return $"status_{status}";
            return Marshal.PtrToStringAnsi(p) ?? $"status_{status}";
        }
        catch
        {
            return $"status_{status}";
        }
    }

    public static IntPtr StringToHGlobalAnsi(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }
}

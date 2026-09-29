using System.Runtime.InteropServices;
using System.Text;
using WorkspaceBorderDetect.Models;

namespace WorkspaceBorderDetect.Interop;

/// <summary>
/// P/Invoke surface for WorkspaceBorderNative.dll C API.
/// </summary>
public static class NativeDetector
{
    public const string DllName = "WorkspaceBorderNative.dll";

    public const int StatusOk = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct WbIntRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public IntRect ToIntRect() => new(Left, Top, Right, Bottom);

        public static WbIntRect From(IntRect r) => new()
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WbDetectRequest
    {
        public IntPtr Bgra;
        public int Width;
        public int Height;
        public int Stride;
        public WbIntRect UserRoi;
        public float DpiX;
        public float DpiY;
        public int OriginX;
        public int OriginY;
        public IntPtr CaptureId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct WbDetectResult
    {
        public int Status;
        public WbIntRect WorkspaceCapture;
        public WbIntRect WorkspaceScreen;
        public int EvidenceGrade;
        public float Confidence;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Message;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SourceCaptureId;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int wb_detect(in WbDetectRequest req, ref WbDetectResult result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr wb_status_name(int status);

    public static string GetStatusName(int status)
    {
        try
        {
            IntPtr p = wb_status_name(status);
            if (p == IntPtr.Zero)
                return $"status_{status}";
            return Marshal.PtrToStringAnsi(p) ?? $"status_{status}";
        }
        catch (DllNotFoundException)
        {
            return $"status_{status}";
        }
        catch (EntryPointNotFoundException)
        {
            return $"status_{status}";
        }
    }

    public static IntPtr StringToHGlobalAnsi(string value)
    {
        // Native API documents const char*; use UTF-8 bytes (ASCII GUID is identical).
        byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }
}

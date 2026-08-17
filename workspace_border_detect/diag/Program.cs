using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

static class Native {
  public const string Dll = "WorkspaceBorderNative.dll";
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Rgt,B; }
  [StructLayout(LayoutKind.Sequential)] public struct Req {
    public IntPtr Bgra; public int W,H,Stride; public R Roi;
    public float DpiX,DpiY; public int Ox,Oy; public IntPtr CapId;
  }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)] public struct Res {
    public int Status; public R Cap, Scr; public int Grade; public float Conf;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Msg;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Src;
  }
  [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern int wb_detect(in Req req, ref Res res);
  [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr wb_status_name(int s);
}

static void Run(string path) {
  using var bmp = new Bitmap(path);
  Console.WriteLine($"\n=== {Path.GetFileName(path)} {bmp.Width}x{bmp.Height} ===");
  var data = bmp.LockBits(new Rectangle(0,0,bmp.Width,bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
  // Use almost-full image as ROI (inset 8px)
  var roi = new Native.R { L=8, T=8, Rgt=bmp.Width-8, B=bmp.Height-8 };
  var id = Marshal.StringToHGlobalAnsi("diag");
  try {
    var req = new Native.Req {
      Bgra=data.Scan0, W=bmp.Width, H=bmp.Height, Stride=data.Stride,
      Roi=roi, DpiX=96, DpiY=96, Ox=0, Oy=0, CapId=id
    };
    var res = new Native.Res { Msg="", Src="" };
    int rc = Native.wb_detect(in req, ref res);
    string name = Marshal.PtrToStringAnsi(Native.wb_status_name(res.Status)) ?? "?";
    Console.WriteLine($"rc={rc} status={res.Status} ({name}) grade={res.Grade} conf={res.Conf:F3}");
    Console.WriteLine($"msg={res.Msg}");
    Console.WriteLine($"capture=[{res.Cap.L},{res.Cap.T},{res.Cap.Rgt},{res.Cap.B})");
  } finally {
    bmp.UnlockBits(data);
    Marshal.FreeHGlobal(id);
  }
}

var dir = args.Length>0 ? args[0] : @"d:\ART_line A\ART_line\workspace_border_detect\native\tests";
foreach (var f in new[]{"case1.png","case2.png","case3.png"}) {
  var p = Path.Combine(dir, f);
  if (File.Exists(p)) Run(p);
}

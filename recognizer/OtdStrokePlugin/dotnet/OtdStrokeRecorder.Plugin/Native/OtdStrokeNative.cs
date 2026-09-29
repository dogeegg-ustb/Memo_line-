using System.Reflection;
using System.Runtime.InteropServices;
using OpenTabletDriver.Plugin;

namespace OtdStrokeRecorder.Plugin.Native;

internal static class OtdStrokeNative
{
    private const string DllName = "otd_stroke";
    private static IntPtr _loadedHandle;

    static OtdStrokeNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(OtdStrokeNative).Assembly, Resolve);
    }

    public static void EnsureLoaded()
    {
        if (_loadedHandle != IntPtr.Zero)
        {
            return;
        }

        var path = FindNativeLibraryPath();
        if (path == null)
        {
            throw new DllNotFoundException(
                "otd_stroke.dll not found. Install zip must contain runtimes/win-x64/native/otd_stroke.dll " +
                "(OpenTabletDriver loads plugins via stream; native libs only from runtimes/).");
        }

        if (!NativeLibrary.TryLoad(path, out _loadedHandle))
        {
            throw new DllNotFoundException($"Failed to load native library: {path}");
        }

        Log.Write("ART Stroke Recorder", $"Loaded native: {path}", LogLevel.Info);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(DllName, StringComparison.OrdinalIgnoreCase) &&
            !libraryName.Equals(DllName + ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        if (_loadedHandle != IntPtr.Zero)
        {
            return _loadedHandle;
        }

        var path = FindNativeLibraryPath();
        if (path != null && NativeLibrary.TryLoad(path, out _loadedHandle))
        {
            return _loadedHandle;
        }

        // Fall back to OTD ALC LoadUnmanagedDll (runtimes/)
        return IntPtr.Zero;
    }

    private static string? FindNativeLibraryPath()
    {
        var candidates = new List<string>();

        // OTD loads managed assemblies from stream, so Assembly.Location is often empty.
        // Native DLL must be under PluginDirectory/.../runtimes/... (see DesktopPluginContext).
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pluginsRoot = Path.Combine(localApp, "OpenTabletDriver", "Plugins");
        if (Directory.Exists(pluginsRoot))
        {
            candidates.AddRange(Directory.GetFiles(pluginsRoot, "otd_stroke.dll", SearchOption.AllDirectories));
        }

        var asmDir = Path.GetDirectoryName(typeof(OtdStrokeNative).Assembly.Location);
        if (!string.IsNullOrEmpty(asmDir))
        {
            candidates.Add(Path.Combine(asmDir, "otd_stroke.dll"));
            candidates.Add(Path.Combine(asmDir, "runtimes", "win-x64", "native", "otd_stroke.dll"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "otd_stroke.dll"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "otd_stroke.dll"));

        return candidates.FirstOrDefault(File.Exists);
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr otd_stroke_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? otdRootUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? deviceNameUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? deviceIdUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void otd_stroke_destroy(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int otd_stroke_start(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void otd_stroke_stop(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void otd_stroke_pen_down(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void otd_stroke_pen_up(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void otd_stroke_on_point(
        IntPtr handle,
        ulong timestampMs,
        double x,
        double y,
        double pressure,
        int inContact,
        uint buttons,
        double tiltX,
        double tiltY,
        ulong sequenceId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void otd_stroke_tick(IntPtr handle, ulong nowMs);
}

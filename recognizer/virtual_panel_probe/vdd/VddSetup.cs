// Root-enumerated device node for an IddCx driver package, the way devcon/nefcon create one.
// Compiled by install-vdd.ps1 / uninstall-vdd.ps1 through Add-Type; needs administrator rights.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class VddSetup
{
    const int DIF_REGISTERDEVICE = 0x19;
    const int DICD_GENERATE_ID = 0x1;
    const int SPDRP_HARDWAREID = 0x1;
    const int DIGCF_ALLCLASSES = 0x4;
    const uint INSTALLFLAG_FORCE = 0x1;
    static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwnd);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid classGuid, string description,
        IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property,
        byte[] buffer, int size);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property,
        out int type, byte[] buffer, int size, out int required);
    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hardwareId, string infPath, uint flags,
        out bool reboot);
    [DllImport("newdev.dll", SetLastError = true)]
    static extern bool DiUninstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA data, int flags, out bool reboot);

    static SP_DEVINFO_DATA NewData()
    {
        var data = new SP_DEVINFO_DATA();
        data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
        return data;
    }

    static Win32Exception Fail(string step) { return new Win32Exception(Marshal.GetLastWin32Error(), step); }

    /// <summary>Device nodes (present or not) whose hardware IDs include `hardwareId`.</summary>
    static List<SP_DEVINFO_DATA> Find(IntPtr set, string hardwareId)
    {
        var found = new List<SP_DEVINFO_DATA>();
        var data = NewData();
        for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
        {
            int type, required;
            var buffer = new byte[2048];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, out type, buffer, buffer.Length, out required))
                continue;
            foreach (string id in Encoding.Unicode.GetString(buffer, 0, Math.Min(required, buffer.Length)).Split('\0'))
                if (string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase)) { found.Add(data); break; }
        }
        return found;
    }

    public static int Count(string hardwareId)
    {
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE) throw Fail("SetupDiGetClassDevs");
        try { return Find(set, hardwareId).Count; }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Creates the device node and installs the INF on it. Returns true when a reboot is required.</summary>
    public static bool Install(string hardwareId, string infPath, Guid classGuid, string className)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == INVALID_HANDLE) throw Fail("SetupDiCreateDeviceInfoList");
        try
        {
            var data = NewData();
            if (!SetupDiCreateDeviceInfoW(set, className, ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref data))
                throw Fail("SetupDiCreateDeviceInfo");
            byte[] ids = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, ids, ids.Length))
                throw Fail("SetupDiSetDeviceRegistryProperty");
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
                throw Fail("SetupDiCallClassInstaller(DIF_REGISTERDEVICE)");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out reboot))
            throw Fail("UpdateDriverForPlugAndPlayDevices");
        return reboot;
    }

    /// <summary>Removes every device node with this hardware ID. Returns how many were removed.</summary>
    public static int Remove(string hardwareId)
    {
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE) throw Fail("SetupDiGetClassDevs");
        try
        {
            int removed = 0;
            foreach (var found in Find(set, hardwareId))
            {
                var data = found;
                bool reboot;
                if (!DiUninstallDevice(IntPtr.Zero, set, ref data, 0, out reboot)) throw Fail("DiUninstallDevice");
                removed++;
            }
            return removed;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}

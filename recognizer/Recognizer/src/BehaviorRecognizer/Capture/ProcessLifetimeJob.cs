using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BehaviorRecognizer.Capture;

/// <summary>Windows owns cleanup of the recorder's helper tree even when the console is forcibly closed.</summary>
internal sealed class ProcessLifetimeJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    public ProcessLifetimeJob()
    {
        _handle=CreateJobObject(0,null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits=new ExtendedLimits { Basic=new BasicLimits { Flags=0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_handle,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            int error=Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(_handle,process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime,JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet,MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority,Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job,int infoClass,ref ExtendedLimits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NaviDnD.Helpers;

// AI requests are separate processes (Claude CLI, which starts its own: node, the MCP server). So that they do not
// linger after the game is closed (the X button, Esc from the menu, a crash), each such process is put into a
// Windows Job Object with KILL_ON_JOB_CLOSE: however the game exits, the system closes its job handle
// and the request's whole process tree ends by itself (child processes join the job automatically).
// macOS/Linux have no job objects: the processes are remembered and their trees killed when the game exits normally, on an
// unhandled exception, or on SIGHUP (terminal window closed) / SIGTERM / SIGINT. A SIGKILL of the game cannot be intercepted;
// then the MCP server still ends when the CLI's pipes close.
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    // The job handle lives until the game process ends (we do not close it — the system closes it on exit).
    private static readonly Lazy<IntPtr> _job = new(Create);

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        return job;
    }

    // Put the request process into the job (if that fails, it just lives as before).
    public static void Add(Process process)
    {
        if (!OperatingSystem.IsWindows()) { AddUnix(process); return; }
        try
        {
            if (_job.Value != IntPtr.Zero) AssignProcessToJobObject(_job.Value, process.Handle);
        }
        catch { /* the process already exited or we lack rights — do not get in the request's way */ }
    }

    // pid → start time: the providers dispose their Process objects, and the start time guards against a reused pid.
    private static readonly ConcurrentDictionary<int, DateTime> _unixChildren = new();
    private static readonly Lazy<PosixSignalRegistration[]> _unixHooks = new(InstallUnixHooks);

    private static void AddUnix(Process process)
    {
        try
        {
            _ = _unixHooks.Value;
            int id = process.Id;
            _unixChildren[id] = process.StartTime;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _unixChildren.TryRemove(id, out _);
            if (process.HasExited) _unixChildren.TryRemove(id, out _);
        }
        catch { /* the process already exited — nothing to clean up */ }
    }

    private static PosixSignalRegistration[] InstallUnixHooks()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillUnixChildren();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => KillUnixChildren();
        // Kept in a static field: a collected registration stops handling the signal. The default action (exit) still runs.
        return [.. new[] { PosixSignal.SIGHUP, PosixSignal.SIGTERM, PosixSignal.SIGINT }
            .Select(signal => PosixSignalRegistration.Create(signal, _ => KillUnixChildren()))];
    }

    private static void KillUnixChildren()
    {
        foreach (var (id, started) in _unixChildren.ToArray())
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.StartTime == started) process.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            _unixChildren.TryRemove(id, out _);
        }
    }
}

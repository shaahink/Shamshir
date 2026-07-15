using System.Runtime.InteropServices;

namespace TradingEngine.CTraderRunner;

/// <summary>
/// Guarantees that child processes (notably <c>ctrader-cli</c> and whatever it spawns) do not
/// outlive the launching process. On Windows it places the current process into a Job Object
/// configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every descendant inherits the job,
/// and when the launcher exits — normally, on crash, or when a test host is torn down — the OS
/// kills the entire tree. This is the robust fix for the recurring orphaned-CLI problem
/// (a <c>ctrader-cli</c> was found alive for two days after its test had finished).
///
/// Call <see cref="EnsureCurrentProcessInKillOnCloseJob"/> once, early, before launching the CLI.
/// It is idempotent and a no-op on non-Windows platforms.
/// </summary>
public static class ChildProcessReaper
{
    private static readonly object Gate = new();
    private static bool _initialized;
    // Held for the process lifetime; closing it would kill-on-close the whole job (including us).
    private static IntPtr _jobHandle = IntPtr.Zero;

    /// <summary>True once the current process has been placed into the kill-on-close job.</summary>
    public static bool IsArmed => _jobHandle != IntPtr.Zero;

    public static void EnsureCurrentProcessInKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows()) return;

        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                var handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) return;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                var length = Marshal.SizeOf(info);
                var infoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
                        return;

                    using var current = System.Diagnostics.Process.GetCurrentProcess();
                    // On Win8+ a process may already belong to a job; nested jobs are allowed and
                    // AssignProcessToJobObject succeeds. If it fails we simply fall back to no reaper.
                    AssignProcessToJobObject(handle, current.Handle);

                    // Keep the handle alive for the process lifetime. Closing it would trigger the
                    // kill-on-close for everything in the job (including us), so we deliberately
                    // hold the handle in a static field and never close it here.
                    _jobHandle = handle;
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }
            }
            catch
            {
                // Best-effort: a missing reaper must never break a real run.
            }
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

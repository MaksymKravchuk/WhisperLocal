using System.Diagnostics;
using System.Runtime.InteropServices;
using static WhisperLocal.Native.NativeMethods;

namespace WhisperLocal.Native;

/// <summary>
/// Keeps child processes (whisper-server) in a job object with KILL_ON_JOB_CLOSE,
/// so they never outlive WhisperLocal, even after a crash.
/// </summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(CreateJob);

    public static void Add(Process process)
    {
        var job = Job.Value;
        if (job == IntPtr.Zero || !AssignProcessToJobObject(job, process.Handle))
            Log.Warn($"Could not assign process {process.Id} to the job object (error {Marshal.GetLastWin32Error()}); it may survive a crash");
    }

    private static IntPtr CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        return job;
    }
}

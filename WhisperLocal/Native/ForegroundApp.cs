using System.Diagnostics;
using static WhisperLocal.Native.NativeMethods;

namespace WhisperLocal.Native;

/// <summary>Snapshot of the foreground window and its process.</summary>
internal sealed record ForegroundApp(IntPtr Hwnd, uint ProcessId, string ProcessName)
{
    public static ForegroundApp Capture()
    {
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var pid);
        string name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch
        {
            name = "";
        }
        return new ForegroundApp(hwnd, pid, name);
    }

    /// <summary>True/false if the token could be read; null if access was denied (usually an elevated or protected process).</summary>
    public bool? IsElevated()
    {
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, ProcessId);
        if (hProcess == IntPtr.Zero)
            return null;
        try
        {
            if (!OpenProcessToken(hProcess, TOKEN_QUERY, out var hToken))
                return null;
            try
            {
                return GetTokenInformation(hToken, TokenElevation, out var elevated, sizeof(int), out _)
                    ? elevated != 0
                    : null;
            }
            finally
            {
                CloseHandle(hToken);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }
}

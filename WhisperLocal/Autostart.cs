using Microsoft.Win32;

namespace WhisperLocal;

/// <summary>Windows logon autostart via the per-user Run registry key (no admin rights needed).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WhisperLocal";

    /// <summary>Creates, updates (exe moved) or removes the Run entry to match the setting.</summary>
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException($"Registry key HKCU\\{RunKey} not found");
        var current = key.GetValue(ValueName) as string;

        if (enabled)
        {
            var command = $"\"{Environment.ProcessPath}\"";
            if (!string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, command);
                Log.Info($"Autostart enabled: {command}");
            }
        }
        else if (current != null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("Autostart disabled");
        }
    }
}

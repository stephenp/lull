using System;
using Microsoft.Win32;

namespace Lull;

/// Launch at Login via HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin rights needed).
/// Windows may also list it under Settings → Apps → Startup, where the user can switch it off;
/// that toggle lives in a separate StartupApproved key, so we check it too.
public static class LaunchAtLogin
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Name = "Lull";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string) return false;
            // StartupApproved: first byte 0x02/0x06 = enabled, 0x03/0x07 = disabled by the user.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(Name) is byte[] b && b.Length > 0 && (b[0] & 0x01) == 0x01) return false;
            return true;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            run.SetValue(Name, Command);
            // Clear a previous "disabled in Task Manager / Settings" flag so turning it on actually works.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(Name, throwOnMissingValue: false);
        }
        else
        {
            run.DeleteValue(Name, throwOnMissingValue: false);
        }
    }

    /// If the exe was moved since Launch at Login was turned on, point the entry at the new path.
    public static void RepairPathIfEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(Name) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                run.SetValue(Name, Command);
        }
        catch (Exception e) { Log.Write($"login item: {e.Message}"); }
    }
}

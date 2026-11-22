using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using static Lull.Native;

namespace Lull;

/// Makes Lull the Windows screen saver. A .scr is just an exe, so this copies Lull.exe to
/// %LOCALAPPDATA%\Lull\Lull.scr and points HKCU\Control Panel\Desktop at it (no admin rights needed).
/// Windows then starts it with /s, /p or /c, which Program.Main hands to ScreenSaverApp / Configure.
public static class ScreenSaverInstall
{
    const string DesktopKey = @"Control Panel\Desktop";
    const string LullKey = @"Software\Lull";
    public const string WallpaperMutex = @"Local\Lull-com.stephenp.lull";

    public static string ScrPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lull", "Lull.scr");

    /// Lull.scr is the screen saver Windows is set to use.
    public static bool IsActive
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
                if (key?.GetValue("SCRNSAVE.EXE") is not string current || current.Length == 0) return false;
                return SamePath(current, ScrPath);
            }
            catch { return false; }
        }
    }

    /// Copies the running exe to Lull.scr, selects it as the screen saver, and opens the Screen Saver
    /// Settings dialog so the user can choose the wait time.
    public static void Install()
    {
        CopySelf();
        using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey))
        {
            // Short (8.3) path where available: older parts of Windows split SCRNSAVE.EXE on spaces.
            key.SetValue("SCRNSAVE.EXE", ShortPath(ScrPath));
            key.SetValue("ScreenSaveActive", "1");
            if (key.GetValue("ScreenSaveTimeOut") is not string t || t == "0" || t.Length == 0)
                key.SetValue("ScreenSaveTimeOut", "600");
        }
        // Tell Windows the settings changed (the registry alone takes effect at next sign-in).
        SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, 1, (string?)null, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        Log.Write($"screen saver: installed {ScrPath}");
        OpenSettings();
    }

    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "desk.cpl,,@screensaver") { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"open screen saver settings: {e.Message}"); }
    }

    /// At wallpaper launch: remember where Lull.exe lives (for the screen saver's Settings button), and if
    /// Lull.scr is in use but came from a different build, refresh it so updates reach the screen saver.
    public static void OnWallpaperLaunch()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(LullKey))
                key.SetValue("AppPath", Environment.ProcessPath ?? "");

            if (!IsActive || IsSelf(ScrPath)) return;
            var self = new FileInfo(Environment.ProcessPath!);
            var scr = new FileInfo(ScrPath);
            if (!scr.Exists || scr.Length != self.Length || scr.LastWriteTimeUtc != self.LastWriteTimeUtc)
            {
                CopySelf();
                Log.Write("screen saver: updated Lull.scr");
            }
        }
        catch (Exception e) { Log.Write($"screen saver: couldn't update Lull.scr: {e.Message}"); }
    }

    /// What Windows' Settings button (/c) does: the screen saver mirrors the wallpaper's settings, so point
    /// the user at the tray menu, and offer to start Lull if it isn't running.
    public static void Configure()
    {
        const string text = "Lull's screen saver shows the same scene as your wallpaper. " +
                            "To change the window, wall or time display, use the Lull icon in the system tray.";
        bool running = Mutex.TryOpenExisting(WallpaperMutex, out var m);
        m?.Dispose();
        string? app = AppPath();
        if (running || app == null)
        {
            MessageBox.Show(text, "Lull Screen Saver", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var r = MessageBox.Show(text + "\n\nLull isn't running. Open it now?", "Lull Screen Saver",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r == DialogResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(app) { UseShellExecute = true }); }
            catch (Exception e) { Log.Write($"screen saver: couldn't start {app}: {e.Message}"); }
        }
    }

    static string? AppPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LullKey);
            return key?.GetValue("AppPath") is string p && File.Exists(p) && !IsSelf(p) ? p : null;
        }
        catch { return null; }
    }

    static void CopySelf()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ScrPath)!);
        File.Copy(Environment.ProcessPath!, ScrPath, overwrite: true);
    }

    static bool IsSelf(string path) => Environment.ProcessPath is string self && SamePath(self, path);

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(LongPath(a)), Path.GetFullPath(LongPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static string ShortPath(string path)
    {
        var sb = new StringBuilder(MAX_PATH);
        return GetShortPathName(path, sb, (uint)sb.Capacity) > 0 ? sb.ToString() : path;
    }

    static string LongPath(string path)
    {
        var sb = new StringBuilder(1024);
        return GetLongPathName(path, sb, (uint)sb.Capacity) > 0 ? sb.ToString() : path;
    }
}

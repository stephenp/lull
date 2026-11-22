using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Lull;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"unhandled: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write($"unobserved: {e.Exception}"); e.SetObserved(); };

        // The same exe is also the screen saver (copied to Lull.scr by ScreenSaverInstall), which Windows
        // starts with /s (full screen), /p <hwnd> (preview in the settings dialog) or /c (Settings button).
        switch (ParseSaverArgs(args, out IntPtr previewParent))
        {
            case SaverCommand.Show:
            {
                using var saver = new Mutex(initiallyOwned: true, @"Local\Lull-saver", out bool firstSaver);
                if (!firstSaver) return;
                Application.Run(new ScreenSaverApp(ScreenSaverApp.Mode.Full));
                return;
            }
            case SaverCommand.Preview:
                Application.Run(new ScreenSaverApp(ScreenSaverApp.Mode.Preview, previewParent));
                return;
            case SaverCommand.Configure:
                ScreenSaverInstall.Configure();
                return;
            case SaverCommand.Ignore:
                return;
        }

        // One Lull at a time: two wallpapers fighting over the desktop would flicker.
        using var single = new Mutex(initiallyOwned: true, ScreenSaverInstall.WallpaperMutex, out bool first);
        if (!first) return;

        Log.Write($"Lull {Application.ProductVersion} starting on {Environment.OSVersion.VersionString}");
        Application.Run(new LullApp());
    }

    enum SaverCommand { None, Show, Preview, Configure, Ignore }

    /// Windows passes "/s", "/p 1234" or "/p:1234", "/c" or "/c:1234", in any case, sometimes with "-".
    /// A .scr started with no arguments (right-click → Configure) means configure.
    static SaverCommand ParseSaverArgs(string[] args, out IntPtr previewParent)
    {
        previewParent = IntPtr.Zero;
        bool isScr = string.Equals(Path.GetExtension(Environment.ProcessPath), ".scr", StringComparison.OrdinalIgnoreCase);
        if (args.Length == 0) return isScr ? SaverCommand.Configure : SaverCommand.None;

        string a = args[0].Trim();
        // Anything unexpected: Lull.exe starts the wallpaper as usual, Lull.scr does nothing.
        var unknown = isScr ? SaverCommand.Ignore : SaverCommand.None;
        if (a.Length < 2 || (a[0] != '/' && a[0] != '-')) return unknown;
        char flag = char.ToLowerInvariant(a[1]);
        string rest = a.Length > 2 && a[2] == ':' ? a[3..] : (args.Length > 1 ? args[1] : "");
        switch (flag)
        {
            case 's':
                return SaverCommand.Show;
            case 'c':
                return SaverCommand.Configure;
            case 'p':
                if (long.TryParse(rest.Trim(), out long hwnd) && hwnd != 0)
                {
                    previewParent = new IntPtr(hwnd);
                    return SaverCommand.Preview;
                }
                return unknown;
            default:
                return unknown;     // e.g. /a, the old Windows 9x password dialog
        }
    }
}

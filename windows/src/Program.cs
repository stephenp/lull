using System;
using System.Threading;
using System.Windows.Forms;

namespace Lull;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // One Lull at a time: two wallpapers fighting over the desktop would flicker.
        using var single = new Mutex(initiallyOwned: true, @"Local\Lull-com.stephenp.lull", out bool first);
        if (!first) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"unhandled: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write($"unobserved: {e.Exception}"); e.SetObserved(); };

        Log.Write($"Lull {Application.ProductVersion} starting on {Environment.OSVersion.VersionString}");
        Application.Run(new LullApp());
    }
}

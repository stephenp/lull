using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using static Lull.Native;

namespace Lull;

/// Lull as a Windows screen saver: what Lull.exe does when Windows starts it as Lull.scr with /s (full
/// screen) or /p <hwnd> (the little preview in the Screen Saver Settings dialog). It shows the same scene
/// as the wallpaper with the same look, read from settings.json (never written: the tray app owns it).
internal sealed class ScreenSaverApp : ApplicationContext
{
    public enum Mode { Full, Preview }

    /// The cursor has to move this far (physical pixels) to end the screen saver, so a nudged desk doesn't.
    const int MoveThreshold = 10;
    /// Input in the first moments is whatever started the screen saver (or the Preview button's click).
    static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(750);

    readonly Mode mode;
    readonly IntPtr previewParent;
    readonly Settings settings = Settings.Load();
    readonly List<SaverWindow> windows = new();
    readonly Timer inputTimer = new() { Interval = 100 };
    readonly DateTime startedAt = DateTime.Now;
    POINT startCursor, lastCursor;
    uint lastInput;
    bool exiting;

    public ScreenSaverApp(Mode mode, IntPtr previewParent = default)
    {
        this.mode = mode;
        this.previewParent = previewParent;
        Log.Write($"screen saver: {mode}" + (mode == Mode.Preview ? $" in 0x{previewParent.ToInt64():X}" : ""));

        // Windows go up (dark) straight away; the scene fades in once WebView2 has loaded it.
        if (mode == Mode.Full)
        {
            foreach (var d in GetDisplays())
                windows.Add(SaverWindow.Fullscreen(d, Lost));
            Cursor.Hide();
            if (windows.Count > 0) SetForegroundWindow(windows[0].Handle);
        }
        else
        {
            var w = SaverWindow.Preview(previewParent, Lost);
            if (w == null)
            {
                // Exiting from the constructor would happen before Application.Run is listening.
                var quit = new Debouncer(1);
                quit.Fire += Exit;
                quit.Trigger();
                return;
            }
            windows.Add(w);
        }

        GetCursorPos(out startCursor);
        lastCursor = startCursor;
        lastInput = LastInputTick();
        inputTimer.Tick += (_, _) => CheckInput();
        inputTimer.Start();

        // Start once the message loop is running, so async continuations land on the UI thread.
        var start = new Debouncer(1);
        start.Fire += () => _ = StartAsync();
        start.Trigger();
    }

    async Task StartAsync()
    {
        CoreWebView2Environment env;
        string webFolder;
        try
        {
            webFolder = WebEnv.PrepareWebFolder();
            // Its own data folder: the wallpaper may be running with different browser options.
            env = await WebEnv.CreateAsync("WebView2-saver", settings.BrowserArguments);
        }
        catch (Exception e)
        {
            // No message boxes from a screen saver: stay dark and let the user dismiss it as usual.
            Log.Write($"screen saver: couldn't start WebView2: {e.Message}");
            return;
        }
        if (exiting) return;
        var json = ConfigJson();
        foreach (var w in windows) w.Start(env, webFolder, json);
    }

    string ConfigJson()
    {
        var loc = settings.LastLocation ?? LocationProvider.EstimateFromTimeZone();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["lat"] = loc.lat,
            ["lon"] = loc.lon,
            ["scene"] = settings.Scene,
            ["decoration"] = settings.Decoration,
            ["wall"] = settings.Wall,
            ["dynamic"] = settings.Dynamic,
            ["speed"] = 1.0,
            ["timeOverride"] = null,
            // The settings dialog's preview is tiny: draw it cheaply and without the time.
            ["showHUD"] = settings.ShowHUD && mode == Mode.Full,
            ["lowPower"] = settings.LowPower || mode == Mode.Preview,
            ["paused"] = false,
        });
    }

    static uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }

    /// WebView2 takes the mouse and keyboard input inside our windows, so watch the system instead:
    /// end when the cursor moves past the threshold, or on any other input (keys, clicks, wheel).
    void CheckInput()
    {
        if (mode == Mode.Preview)
        {
            if (!IsWindow(previewParent)) Exit();      // the dialog closed (it doesn't always tell us)
            return;
        }

        GetCursorPos(out POINT cursor);
        uint input = LastInputTick();
        if (DateTime.Now - startedAt < Grace)
        {
            startCursor = lastCursor = cursor;
            lastInput = input;
            return;
        }

        int dx = cursor.X - startCursor.X, dy = cursor.Y - startCursor.Y;
        bool moved = dx * dx + dy * dy > MoveThreshold * MoveThreshold;
        // New input without the cursor moving since the last tick is a key, click or wheel. (Small cursor
        // jitter also counts as input, which is why movement alone has its own threshold.)
        bool other = input != lastInput && cursor.X == lastCursor.X && cursor.Y == lastCursor.Y;
        lastCursor = cursor;
        lastInput = input;
        if (moved || other) Exit();
    }

    /// One of our windows was closed or deactivated from outside.
    void Lost()
    {
        if (mode == Mode.Full && DateTime.Now - startedAt < Grace) return;
        Exit();
    }

    void Exit()
    {
        if (exiting) return;
        exiting = true;
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        inputTimer.Stop();
        foreach (var w in windows) w.Dispose();
        windows.Clear();
        if (mode == Mode.Full) Cursor.Show();
        base.ExitThreadCore();
    }
}

/// One screen saver window: full screen and topmost on a monitor, or a child of the preview box in the
/// Screen Saver Settings dialog. Hosts the scene in a SceneView.
internal sealed class SaverWindow : NativeWindow, IDisposable
{
    readonly bool preview;
    readonly Action lost;
    Display display;
    SceneView? scene;
    bool disposed;

    SaverWindow(CreateParams cp, bool preview, Display display, Action lost)
    {
        this.preview = preview;
        this.display = display;
        this.lost = lost;
        CreateHandle(cp);
    }

    public static SaverWindow Fullscreen(Display d, Action lost)
    {
        var w = new SaverWindow(new CreateParams
        {
            Caption = "Lull Screen Saver",
            Style = unchecked((int)(WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN)),
            ExStyle = (int)WS_EX_TOPMOST,
            X = d.Bounds.Left, Y = d.Bounds.Top, Width = d.Bounds.Width, Height = d.Bounds.Height,
        }, preview: false, d, lost);
        SetWindowPos(w.Handle, HWND_TOPMOST, d.Bounds.Left, d.Bounds.Top, d.Bounds.Width, d.Bounds.Height, SWP_SHOWWINDOW);
        return w;
    }

    public static SaverWindow? Preview(IntPtr parent, Action lost)
    {
        if (!IsWindow(parent) || !GetClientRect(parent, out RECT r)) return null;
        IntPtr monitor = MonitorFromWindow(parent, MONITOR_DEFAULTTONEAREST);
        var displays = GetDisplays();
        var d = displays.FirstOrDefault(x => x.Handle == monitor);
        if (d.Device == null) d = displays.FirstOrDefault();
        return new SaverWindow(new CreateParams
        {
            Caption = "Lull Preview",
            // Disabled, so clicks in the dialog's preview box never reach the scene.
            Style = unchecked((int)(WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_DISABLED)),
            Parent = parent,
            X = 0, Y = 0, Width = r.Width, Height = r.Height,
        }, preview: true, d, lost);
    }

    public void Start(CoreWebView2Environment env, string webFolder, string configJson)
    {
        if (disposed) return;
        double scale = display.Device == null ? 1.0 : display.Scale;
        scene = SceneView.Start(Handle, preview ? "saver preview" : $"saver {display.Device}", scale, env, webFolder,
            loaded: () =>
            {
                scene?.Execute("document.documentElement.style.cursor = document.body.style.cursor = 'none';");
                scene?.Push(configJson);
            },
            failed: dead => Log.Write($"screen saver: scene failed{(dead ? " (browser exited)" : "")}"));
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_ERASEBKGND:
                using (var g = Graphics.FromHdc(m.WParam)) g.Clear(SceneView.SceneBase);
                m.Result = new IntPtr(1);
                return;
            case WM_SETCURSOR when !preview:
                SetCursor(IntPtr.Zero);
                m.Result = new IntPtr(1);
                return;
            case WM_SIZE:
                base.WndProc(ref m);
                scene?.UpdateBounds();
                return;
            case WM_DPICHANGED when !preview:
                // Keep covering exactly our monitor; just follow its new scale.
                if (scene != null) scene.Scale = (m.WParam.ToInt64() & 0xFFFF) / 96.0;
                var b = display.Bounds;
                SetWindowPos(Handle, HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, SWP_NOACTIVATE);
                m.Result = IntPtr.Zero;
                return;
            case WM_ACTIVATEAPP when !preview && m.WParam == IntPtr.Zero:
                base.WndProc(ref m);
                lost();                      // another app came to the front
                return;
            case WM_DESTROY:
                base.WndProc(ref m);
                if (!disposed) lost();       // e.g. the settings dialog destroyed our preview
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        scene?.Dispose();
        scene = null;
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}

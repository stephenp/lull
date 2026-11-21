using System;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using static Lull.Native;

namespace Lull;

/// One wallpaper window per monitor: a plain Win32 window parented behind the desktop icons, hosting a
/// WebView2 controller that runs web/index.html. Mirrors mac/Sources/WallpaperWindow.swift.
///
/// We host CoreWebView2Controller directly (not the WinForms WebView2 control) so we own its bounds,
/// DPI scale and visibility: once our window is a child of Explorer's desktop it no longer gets
/// WM_DPICHANGED, so the scale has to be set by hand.
internal sealed class WallpaperHost : NativeWindow, IDisposable
{
    public const string HostName = "lull.example";        // virtual host mapped to the unpacked web folder
    static readonly Color SceneBase = Color.FromArgb(0x07, 0x08, 0x0c);   // body background in web/src.html

    public Display Display { get; private set; }
    public bool IsLoaded { get; private set; }
    public DateTime CreatedAt { get; } = DateTime.Now;
    /// Called when the scene says it's ready (and again after every reload).
    public Action<WallpaperHost>? Loaded;
    /// Called when this host can't recover by itself. `environmentDead` means the shared WebView2
    /// browser process is gone, so the environment has to be recreated before trying again.
    public Action<WallpaperHost, bool>? Failed;

    Desktop.Handles desktop;
    CoreWebView2Controller? controller;
    bool covered, asleep;
    bool? sentPaused;
    bool disposed;

    WallpaperHost(Display display, Desktop.Handles desktop)
    {
        Display = display;
        this.desktop = desktop;
        var cp = new CreateParams
        {
            Caption = "Lull Wallpaper",
            Style = unchecked((int)(WS_POPUP | WS_CLIPCHILDREN)),
            ExStyle = (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),   // never in the taskbar, Alt-Tab or Task View
            X = display.Bounds.Left, Y = display.Bounds.Top,
            Width = display.Bounds.Width, Height = display.Bounds.Height,
        };
        CreateHandle(cp);
    }

    /// Creates the window, attaches it to the desktop, then starts WebView2 (in that order: WebView2
    /// initialisation can stall if its parent window isn't shown yet). Returns null if attaching failed.
    public static WallpaperHost? Create(Display display, Desktop.Handles desktop, CoreWebView2Environment env, string webFolder,
                                        Action<WallpaperHost> loaded, Action<WallpaperHost, bool> failed, bool asleep)
    {
        var host = new WallpaperHost(display, desktop) { Loaded = loaded, Failed = failed, asleep = asleep };
        if (!Desktop.Attach(host.Handle, desktop, display))
        {
            host.Dispose();
            return null;
        }
        _ = host.InitWebViewAsync(env, webFolder);
        return host;
    }

    async Task InitWebViewAsync(CoreWebView2Environment env, string webFolder)
    {
        try
        {
            var c = await env.CreateCoreWebView2ControllerAsync(Handle);
            if (disposed) { c.Close(); return; }
            controller = c;
            c.DefaultBackgroundColor = SceneBase;              // no white flash at login
            c.ShouldDetectMonitorScaleChanges = false;
            c.RasterizationScale = Display.Scale;
            UpdateBounds();

            var wv = c.CoreWebView2;
            var s = wv.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("LULL_DEVTOOLS") == "1";

            wv.SetVirtualHostNameToFolderMapping(HostName, webFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            wv.WebMessageReceived += OnWebMessage;
            wv.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) Log.Write($"host {Display.Device}: navigation failed ({e.WebErrorStatus})");
            };
            wv.ProcessFailed += (_, e) =>
            {
                Log.Write($"host {Display.Device}: WebView2 process failed ({e.ProcessFailedKind}, {e.Reason})");
                switch (e.ProcessFailedKind)
                {
                    case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                        Failed?.Invoke(this, true);
                        break;
                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                        Reload();                      // just this monitor; posts "ready" again
                        break;
                    default:
                        break;                         // GPU / utility processes restart on their own
                }
            };
            // Keep the scene on-site: it has no links, but never navigate anywhere else.
            wv.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            wv.NewWindowRequested += (_, e) => e.Handled = true;

            // Version in the URL so a new build never shows a cached copy of the old scene.
            wv.Navigate($"https://{HostName}/index.html?v={Uri.EscapeDataString(Application.ProductVersion)}");
            if (asleep) await ApplySleepAsync(true);   // created while the displays were off / locked
        }
        catch (Exception e)
        {
            Log.Write($"host {Display.Device}: WebView2 init failed: {e.Message}");
            if (!disposed) Failed?.Invoke(this, true);
        }
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
            {
                // "ready" guarantees window.lull exists (NavigationCompleted can fire before the script runs).
                IsLoaded = true;
                sentPaused = null;
                Loaded?.Invoke(this);
                UpdatePaused();
            }
        }
        catch (JsonException) { }
    }

    public void Push(string configJson)
    {
        if (!IsLoaded || controller == null) return;
        _ = Run($"window.lull && window.lull.setConfig({configJson});");
    }

    async Task Run(string js)
    {
        try { if (controller != null) await controller.CoreWebView2.ExecuteScriptAsync(js); }
        catch (Exception e) { Log.Write($"host {Display.Device}: script failed: {e.Message}"); }
    }

    /// Desktop on this monitor is fully covered by other windows.
    public bool Covered { get => covered; set { if (covered != value) { covered = value; UpdatePaused(); } } }

    /// Displays are off, the session is locked, or the PC is suspending.
    public bool Asleep
    {
        get => asleep;
        set
        {
            if (asleep == value) return;
            asleep = value;
            if (value) { UpdatePaused(); _ = ApplySleepAsync(true); }
            else _ = WakeAsync();
        }
    }

    void UpdatePaused()
    {
        if (!IsLoaded) return;
        bool paused = asleep || covered;
        if (paused == sentPaused) return;
        sentPaused = paused;
        _ = Run($"window.lull && window.lull.setConfig({{paused: {(paused ? "true" : "false")}}});");
    }

    async Task WakeAsync()
    {
        await ApplySleepAsync(false);   // un-suspend first, then let the scene draw again
        UpdatePaused();
    }

    /// For long pauses, also hide and suspend the WebView so it gives back CPU and memory.
    async Task ApplySleepAsync(bool sleep)
    {
        var c = controller;
        if (c == null) return;
        try
        {
            if (sleep)
            {
                c.IsVisible = false;
                c.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                await c.CoreWebView2.TrySuspendAsync();
            }
            else
            {
                c.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                c.IsVisible = true;          // also resumes a suspended WebView
            }
        }
        catch (Exception e) { Log.Write($"host {Display.Device}: sleep({sleep}) failed: {e.Message}"); }
    }

    /// Re-fit to the monitor (after a resolution / scale / arrangement change that kept the same monitors).
    public void Refit(Display display)
    {
        Display = display;
        Desktop.Position(Handle, desktop, display);
        if (controller != null) controller.RasterizationScale = display.Scale;
        UpdateBounds();
    }

    void UpdateBounds()
    {
        if (controller == null || !GetClientRect(Handle, out RECT r)) return;
        controller.Bounds = new Rectangle(0, 0, r.Width, r.Height);
        controller.NotifyParentWindowPositionChanged();
    }

    public void Reload()
    {
        IsLoaded = false;
        try { controller?.CoreWebView2.Reload(); }
        catch (Exception e) { Log.Write($"host {Display.Device}: reload failed: {e.Message}"); Failed?.Invoke(this, false); }
    }

    /// Explorer replaced its wallpaper WorkerW (raised layout): we survive as a child of Progman,
    /// but need to go back between the icons and the new WorkerW.
    public void Reinsert(Desktop.Handles h)
    {
        desktop = h;
        Desktop.Reinsert(Handle, h);
    }

    /// Our window isn't exactly over its monitor (e.g. Explorer moved its desktop window after a display change).
    public bool IsMisplaced
    {
        get
        {
            if (!GetWindowRect(Handle, out RECT r)) return false;
            var b = Display.Bounds;
            return r.Left != b.Left || r.Top != b.Top || r.Width != b.Width || r.Height != b.Height;
        }
    }

    public void Reposition() => Desktop.Position(Handle, desktop, Display);

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_ERASEBKGND:
                using (var g = Graphics.FromHdc(m.WParam)) g.Clear(SceneBase);
                m.Result = new IntPtr(1);
                return;
            case WM_MOUSEACTIVATE:
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            case WM_SIZE:
                base.WndProc(ref m);
                UpdateBounds();
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { controller?.Close(); } catch { }
        controller = null;
        if (Handle != IntPtr.Zero)
        {
            Desktop.Detach(Handle);
            DestroyHandle();
        }
    }
}

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using static Lull.Native;

namespace Lull;

/// One wallpaper window per monitor: a plain Win32 window parented behind the desktop icons, hosting the
/// scene in a SceneView. Mirrors mac/Sources/WallpaperWindow.swift.
internal sealed class WallpaperHost : NativeWindow, IDisposable
{
    public Display Display { get; private set; }
    public bool IsLoaded => scene?.IsLoaded == true;
    public DateTime CreatedAt { get; } = DateTime.Now;
    /// Called when the scene says it's ready (and again after every reload).
    public Action<WallpaperHost>? Loaded;
    /// Called when this host can't recover by itself. `environmentDead` means the shared WebView2
    /// browser process is gone, so the environment has to be recreated before trying again.
    public Action<WallpaperHost, bool>? Failed;

    Desktop.Handles desktop;
    SceneView? scene;
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
        host.scene = SceneView.Start(host.Handle, $"host {display.Device}", display.Scale, env, webFolder,
            loaded: () =>
            {
                host.sentPaused = null;
                host.Loaded?.Invoke(host);
                host.UpdatePaused();
            },
            failed: dead => { if (!host.disposed) host.Failed?.Invoke(host, dead); },
            initialized: async () => { if (host.asleep) await host.ApplySleepAsync(true); });   // created while the displays were off / locked
        return host;
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
        scene?.SetPaused(paused);
    }

    async Task WakeAsync()
    {
        await ApplySleepAsync(false);   // un-suspend first, then let the scene draw again
        UpdatePaused();
    }

    /// For long pauses, also hide and suspend the WebView so it gives back CPU and memory.
    async Task ApplySleepAsync(bool sleep)
    {
        var c = scene?.Controller;
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
        if (scene != null) { scene.Scale = display.Scale; scene.UpdateBounds(); }
    }

    public void Push(string configJson) => scene?.Push(configJson);

    public void Reload() => scene?.Reload();

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
                using (var g = Graphics.FromHdc(m.WParam)) g.Clear(SceneView.SceneBase);
                m.Result = new IntPtr(1);
                return;
            case WM_MOUSEACTIVATE:
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            case WM_SIZE:
                base.WndProc(ref m);
                scene?.UpdateBounds();
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
        if (Handle != IntPtr.Zero)
        {
            Desktop.Detach(Handle);
            DestroyHandle();
        }
    }
}

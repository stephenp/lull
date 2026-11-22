using System;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using static Lull.Native;

namespace Lull;

/// The scene (web/index.html) in a WebView2 controller filling a window we own. Shared by the wallpaper
/// (WallpaperHost) and the screen saver (SaverWindow).
///
/// We host CoreWebView2Controller directly (not the WinForms WebView2 control) so we own its bounds,
/// DPI scale and visibility: a wallpaper window that is a child of Explorer's desktop no longer gets
/// WM_DPICHANGED, so the scale has to be set by hand.
internal sealed class SceneView : IDisposable
{
    public const string HostName = "lull.example";        // virtual host mapped to the unpacked web folder
    public static readonly Color SceneBase = Color.FromArgb(0x07, 0x08, 0x0c);   // body background in web/src.html

    readonly IntPtr owner;
    readonly string label;
    readonly Action loaded;
    readonly Action<bool> failed;
    bool disposed;

    public CoreWebView2Controller? Controller { get; private set; }
    public bool IsLoaded { get; private set; }

    SceneView(IntPtr owner, string label, Action loaded, Action<bool> failed)
    {
        this.owner = owner;
        this.label = label;
        this.loaded = loaded;
        this.failed = failed;
    }

    /// `loaded` runs when the scene says it's ready (and again after every reload). `failed` runs when the
    /// view can't recover by itself; true means the shared browser process is gone and the environment
    /// has to be recreated. `initialized` runs once the controller exists and navigation has started.
    public static SceneView Start(IntPtr owner, string label, double scale, CoreWebView2Environment env, string webFolder,
                                  Action loaded, Action<bool> failed, Func<Task>? initialized = null)
    {
        var view = new SceneView(owner, label, loaded, failed);
        _ = view.InitAsync(scale, env, webFolder, initialized);
        return view;
    }

    async Task InitAsync(double scale, CoreWebView2Environment env, string webFolder, Func<Task>? initialized)
    {
        try
        {
            var c = await env.CreateCoreWebView2ControllerAsync(owner);
            if (disposed) { c.Close(); return; }
            Controller = c;
            c.DefaultBackgroundColor = SceneBase;              // no white flash at login
            c.ShouldDetectMonitorScaleChanges = false;
            c.RasterizationScale = scale;
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
                if (!e.IsSuccess) Log.Write($"{label}: navigation failed ({e.WebErrorStatus})");
            };
            wv.ProcessFailed += (_, e) =>
            {
                Log.Write($"{label}: WebView2 process failed ({e.ProcessFailedKind}, {e.Reason})");
                switch (e.ProcessFailedKind)
                {
                    case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                        failed(true);
                        break;
                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                        Reload();                      // just this view; posts "ready" again
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
            if (initialized != null) await initialized();
        }
        catch (Exception e)
        {
            Log.Write($"{label}: WebView2 init failed: {e.Message}");
            if (!disposed) failed(true);
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
                loaded();
            }
        }
        catch (JsonException) { }
    }

    public void Push(string configJson)
    {
        if (!IsLoaded || Controller == null) return;
        _ = Run($"window.lull && window.lull.setConfig({configJson});");
    }

    public void SetPaused(bool paused)
    {
        if (!IsLoaded) return;
        _ = Run($"window.lull && window.lull.setConfig({{paused: {(paused ? "true" : "false")}}});");
    }

    /// Runs a script in the scene once it's loaded (fire and forget; failures are logged).
    public void Execute(string js)
    {
        if (IsLoaded) _ = Run(js);
    }

    async Task Run(string js)
    {
        try { if (Controller != null) await Controller.CoreWebView2.ExecuteScriptAsync(js); }
        catch (Exception e) { Log.Write($"{label}: script failed: {e.Message}"); }
    }

    public double Scale { set { if (Controller != null) Controller.RasterizationScale = value; } }

    /// Fit the controller to the owner window's client area.
    public void UpdateBounds()
    {
        if (Controller == null || !GetClientRect(owner, out RECT r)) return;
        Controller.Bounds = new Rectangle(0, 0, r.Width, r.Height);
        Controller.NotifyParentWindowPositionChanged();
    }

    public void Reload()
    {
        IsLoaded = false;
        try { Controller?.CoreWebView2.Reload(); }
        catch (Exception e) { Log.Write($"{label}: reload failed: {e.Message}"); failed(false); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Controller?.Close(); } catch { }
        Controller = null;
    }
}

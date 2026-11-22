using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using static Lull.Native;

namespace Lull;

/// The tray app. Mirrors mac/Sources/AppDelegate.swift: owns the per-monitor wallpaper windows, the
/// tray menu, Preview Day, location, and everything that pauses or rebuilds the wallpaper.
internal sealed class LullApp : ApplicationContext
{
    readonly Settings settings = Settings.Load();
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip menu = new();
    readonly MessageWindow messages;
    readonly List<WallpaperHost> hosts = new();
    readonly uint ownPid = (uint)Environment.ProcessId;

    CoreWebView2Environment? env;
    string webFolder = "";
    Desktop.Handles? desktop;
    WorkerWWatcher? workerWWatcher;
    CoverageEvents? coverageEvents;
    string displaySignature = "";

    // pause state
    bool displaysOff, locked, suspended;
    bool Asleep => displaysOff || locked || suspended;

    // location
    string? locationProblem;

    // Preview Day (same model as the Mac app)
    const double PreviewSpeed = 1440;     // 24 hours in ~60 seconds
    bool previewDay, previewPlaying = true;
    double previewBase;                   // minutes since local midnight at previewSetAt
    DateTime previewSetAt = DateTime.Now;
    PreviewForm? previewForm;
    bool scrubPushPending;

    // timers
    readonly Timer coverageTimer = new() { Interval = 1000 };
    readonly Timer previewTimer = new() { Interval = 100 };
    readonly Timer locationTimer = new() { Interval = 3 * 60 * 60 * 1000 };
    readonly Debouncer rebuildSoon = new(500);
    readonly Debouncer refitSoon = new(1500);
    readonly Debouncer coverageSoon = new(250);
    readonly Debouncer workerWSoon = new(500);
    int healthTicks;

    // Explorer restart loop protection
    readonly Queue<DateTime> recentResets = new();
    uint lastExplorerPid;
    bool gaveUp;

    // WebView failure backoff (browser crashes, init failures, monitors that never load)
    static readonly int[] FailureDelays = { 1500, 5000, 30000 };
    int failures;

    public LullApp()
    {
        messages = new MessageWindow(this);

        tray.Icon = TrayIconArt.Make();
        tray.Text = "Lull";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseUp += (_, e) =>
        {
            // Left click opens the menu too, like the Mac menu bar icon.
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(tray, null);
        };
        menu.Opening += (_, _) => BuildMenu();
        ApplyMenuTheme();
        BuildMenu();   // so the first open has content even before Opening fires

        coverageTimer.Tick += (_, _) => { UpdateCoverage(); HealthCheck(); };
        previewTimer.Tick += (_, _) => PreviewTick();
        locationTimer.Tick += (_, _) => _ = RefreshLocationAsync();
        rebuildSoon.Fire += () => _ = RebuildAsync();
        refitSoon.Fire += RepositionAll;
        coverageSoon.Fire += UpdateCoverage;
        workerWSoon.Fire += WorkerWReplaced;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Application.ApplicationExit += (_, _) => Cleanup();

        LaunchAtLogin.RepairPathIfEnabled();
        ScreenSaverInstall.OnWallpaperLaunch();
        // Start once the message loop is running, so async continuations land on the UI thread.
        var start = new Debouncer(1);
        start.Fire += () => _ = StartAsync();
        start.Trigger();
    }

    async Task StartAsync()
    {
        try
        {
            webFolder = WebEnv.PrepareWebFolder();
            if (!await EnsureEnvironmentAsync()) return;

            coverageEvents = new CoverageEvents(() => coverageSoon.Trigger());
            coverageTimer.Start();      // also drives the health check, so start it before anything can fail
            Rebuild();

            // Always follow the real location; refresh every few hours (travel, DST, etc.).
            locationTimer.Start();
            await RefreshLocationAsync();
        }
        catch (Exception e)
        {
            Log.Write($"startup failed: {e}");
            if (env == null)
            {
                MessageBox.Show($"Lull couldn't start: {e.Message}", "Lull", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitThread();
            }
            else ScheduleFailureRebuild();
        }
    }

    /// Creates the shared WebView2 environment (one browser + GPU process for every monitor).
    /// Returns false, after telling the user, if the WebView2 runtime is missing.
    async Task<bool> EnsureEnvironmentAsync()
    {
        if (env != null) return true;
        try
        {
            var e = await WebEnv.CreateAsync("WebView2", settings.BrowserArguments);
            e.BrowserProcessExited += (_, _) =>
            {
                Log.Write("WebView2 browser process exited");
                if (env == e) { env = null; ScheduleFailureRebuild(); }
            };
            env = e;
            Log.Write($"WebView2 runtime {e.BrowserVersionString}");
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var r = MessageBox.Show(
                "Lull needs the Microsoft Edge WebView2 Runtime, which isn't installed on this PC.\n\nOpen the download page now?",
                "Lull", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
            ExitThread();
            return false;
        }
    }

    // MARK: Wallpaper windows

    async Task RebuildAsync()
    {
        try
        {
            if (env == null && !await EnsureEnvironmentAsync()) return;
            Rebuild();
        }
        catch (Exception e)
        {
            Log.Write($"rebuild failed: {e}");
            ScheduleFailureRebuild();
        }
    }

    void Rebuild()
    {
        if (env == null) { rebuildSoon.Trigger(); return; }
        if (gaveUp) return;
        TearDownHosts();

        desktop = Desktop.Discover(settings.ParentToWorkerW);
        Log.Write("desktop: " + Desktop.Describe(desktop));
        if (desktop == null)
        {
            Log.Write("desktop: Explorer's desktop windows not found yet; retrying");
            rebuildSoon.Trigger(2000);
            return;
        }
        lastExplorerPid = desktop.ExplorerPid;
        workerWWatcher = new WorkerWWatcher(desktop, () => workerWSoon.Trigger());

        var displays = GetDisplays();
        displaySignature = Signature(displays);
        foreach (var d in displays)
        {
            var host = WallpaperHost.Create(d, desktop, env, webFolder, HostLoaded, HostFailed, Asleep);
            if (host == null) { Log.Write($"desktop: couldn't attach on {d.Device}"); continue; }
            hosts.Add(host);
            Log.Write($"host {d.Device}: {d.Bounds} @ {d.Dpi} dpi");
        }
        UpdateCoverage();
    }

    void HostLoaded(WallpaperHost h)
    {
        failures = 0;
        h.Push(ConfigJson());
    }

    void HostFailed(WallpaperHost h, bool environmentDead)
    {
        if (environmentDead && env != null)
        {
            env = null;    // recreate the browser process before trying again
        }
        ScheduleFailureRebuild();
    }

    /// Rebuild after a WebView failure, backing off and eventually giving up so a persistent problem
    /// (broken runtime, unwritable data folder) doesn't churn forever.
    void ScheduleFailureRebuild()
    {
        if (gaveUp || rebuildSoon.Pending) return;     // several monitors failing at once is one incident
        if (failures >= FailureDelays.Length)
        {
            gaveUp = true;
            TearDownHosts();
            Log.Write("WebView2 keeps failing; wallpaper stopped");
            ShowBalloon("Lull stopped", "The wallpaper kept failing to load, so Lull stopped trying. Choose Reload Wallpaper to try again.");
            return;
        }
        rebuildSoon.Trigger(FailureDelays[failures++]);
    }

    /// Explorer destroyed the WorkerW we watch. In the raised layout our windows (children of Progman)
    /// survive, so just put them back between the icons and the new WorkerW instead of reloading.
    void WorkerWReplaced()
    {
        if (desktop is { Layout: Desktop.Layout.Raised } old && old.Parent == old.Progman && hosts.Count > 0)
        {
            var fresh = Desktop.Discover(settings.ParentToWorkerW);
            if (fresh != null && fresh.Progman == old.Progman && hosts.All(h => Desktop.IsStillAttached(h.Handle, fresh)))
            {
                desktop = fresh;
                foreach (var h in hosts) h.Reinsert(fresh);
                workerWWatcher?.Dispose();
                workerWWatcher = new WorkerWWatcher(fresh, () => workerWSoon.Trigger());
                Log.Write("desktop: WorkerW replaced; re-inserted");
                return;
            }
        }
        Log.Write("desktop: WorkerW destroyed; rebuilding");
        rebuildSoon.Trigger();
    }

    void RepositionAll()
    {
        foreach (var h in hosts.Where(h => h.IsMisplaced)) h.Reposition();
    }

    void TearDownHosts()
    {
        workerWWatcher?.Dispose();
        workerWWatcher = null;
        foreach (var h in hosts) h.Dispose();
        hosts.Clear();
    }

    static string Signature(Display[] ds) => string.Join("|", ds.Select(d => d.Signature));

    /// Resolution, scale, arrangement or monitor count may have changed.
    void DisplaysMaybeChanged()
    {
        var ds = GetDisplays();
        var sig = Signature(ds);
        if (sig == displaySignature) return;
        if (ds.Length == hosts.Count && desktop != null && ds.Select(d => d.Device).SequenceEqual(hosts.Select(h => h.Display.Device)))
        {
            // Same monitors, new geometry: just re-fit.
            for (int i = 0; i < ds.Length; i++) hosts[i].Refit(ds[i]);
            Desktop.EnsureWorkerWAtBottom(desktop);
            displaySignature = sig;
            refitSoon.Trigger();   // Explorer may still be resizing its desktop window: check again shortly
            Log.Write("displays changed: re-fit");
        }
        else
        {
            Log.Write("displays changed: rebuild");
            rebuildSoon.Trigger();
        }
    }

    /// Explorer restarted (crash, or "Restart" in Task Manager): its desktop windows are new.
    public void ExplorerRestarted()
    {
        // TaskbarCreated also fires on taskbar DPI changes; only a new Explorer process matters.
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        uint pid = tray == IntPtr.Zero ? 0 : ProcessOf(tray);
        if (pid != 0 && pid == lastExplorerPid && desktop != null && hosts.All(h => Desktop.IsStillAttached(h.Handle, desktop)))
            return;

        var now = DateTime.Now;
        recentResets.Enqueue(now);
        while (recentResets.Count > 0 && now - recentResets.Peek() > TimeSpan.FromSeconds(60)) recentResets.Dequeue();
        if (recentResets.Count > 5)
        {
            gaveUp = true;
            TearDownHosts();
            Log.Write("Explorer keeps restarting; wallpaper stopped");
            ShowBalloon("Lull stopped", "Windows Explorer keeps restarting, so Lull paused the wallpaper. Choose Reload Wallpaper to try again.");
            return;
        }
        Log.Write("Explorer restarted: rebuilding");
        rebuildSoon.Trigger(1000);
    }

    /// Every few seconds: make sure Explorer hasn't pulled our windows out from under us.
    void HealthCheck()
    {
        if (++healthTicks % 5 != 0 || env == null || gaveUp) return;
        if (desktop == null || !desktop.StillValid || hosts.Any(h => !Desktop.IsStillAttached(h.Handle, desktop)))
        {
            Log.Write("health: desktop windows changed; rebuilding");
            rebuildSoon.Trigger();
            return;
        }
        Desktop.EnsureWorkerWAtBottom(desktop);
        DisplaysMaybeChanged();
        RepositionAll();

        // A monitor without a wallpaper (attach failed) or one whose scene never loaded: try again.
        bool missing = hosts.Count < GetDisplays().Length;
        bool stuck = hosts.Any(h => !h.IsLoaded && !h.Asleep && DateTime.Now - h.CreatedAt > TimeSpan.FromSeconds(20));
        if ((missing || stuck) && !rebuildSoon.Pending)
        {
            Log.Write($"health: {(missing ? "monitor without wallpaper" : "scene didn't load")}; retrying");
            ScheduleFailureRebuild();
        }
    }

    // MARK: Pausing

    void UpdateCoverage()
    {
        if (hosts.Count == 0) return;
        if (!settings.PauseWhenCovered || Asleep)
        {
            foreach (var h in hosts) h.Covered = false;
            return;
        }
        var covered = Coverage.Compute(hosts.Select(h => h.Display).ToArray(), ownPid);
        for (int i = 0; i < hosts.Count; i++) hosts[i].Covered = covered[i];
    }

    void SetAsleepState()
    {
        foreach (var h in hosts) h.Asleep = Asleep;
        if (!Asleep) { PushConfig(); UpdateCoverage(); }
    }

    public void DisplayPower(bool on) { displaysOff = !on; SetAsleepState(); }

    public void Suspending() { suspended = true; SetAsleepState(); }

    public void Resumed()
    {
        suspended = false;
        SetAsleepState();
        _ = RefreshLocationAsync();
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => DisplaysMaybeChanged();

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Light/dark theme switches arrive as General.
        if (e.Category == UserPreferenceCategory.General) { RefreshTrayIcon(); ApplyMenuTheme(); }
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect)
        {
            locked = true;
            SetAsleepState();
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect)
        {
            locked = false;
            SetAsleepState();
            // Explorer sometimes replaces its WorkerW across a lock.
            if (desktop == null || !desktop.StillValid) rebuildSoon.Trigger();
        }
    }

    // MARK: Config

    string ConfigJson()
    {
        var loc = EffectiveLocation();
        var dict = new Dictionary<string, object?>
        {
            ["lat"] = loc.lat,
            ["lon"] = loc.lon,
            ["scene"] = settings.Scene,
            ["decoration"] = settings.Decoration,
            ["wall"] = settings.Wall,
            ["dynamic"] = settings.Dynamic,
            ["speed"] = previewDay ? (previewPlaying ? PreviewSpeed : 0.0) : 1.0,
            ["showHUD"] = settings.ShowHUD || previewDay,
            ["lowPower"] = settings.LowPower,
            ["timeOverride"] = null,
        };
        if (previewDay)
        {
            double minutes = PreviewMinutesNow() % 1440;
            long midnight = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
            dict["timeOverride"] = midnight + minutes * 60_000.0;
        }
        return JsonSerializer.Serialize(dict);
    }

    (double lat, double lon) EffectiveLocation() => settings.LastLocation ?? LocationProvider.EstimateFromTimeZone();

    async Task RefreshLocationAsync()
    {
        var r = await LocationProvider.RequestAsync();
        if (r.Ok)
        {
            settings.LastLocation = (r.Lat, r.Lon);
            settings.Save();
            locationProblem = null;
        }
        else
        {
            Log.Write("location: " + r.Problem);
            locationProblem = r.Problem;
        }
        PushConfig();
    }

    void PushConfig()
    {
        if (previewDay)
        {
            // Re-anchor the preview clock so the scene and the scrubber stay in lockstep.
            previewBase = PreviewMinutesNow() % 1440;
            previewSetAt = DateTime.Now;
        }
        var json = ConfigJson();
        foreach (var h in hosts) h.Push(json);
    }

    void Changed() { settings.Save(); PushConfig(); }

    // MARK: Tray menu

    void BuildMenu()
    {
        menu.SuspendLayout();
        var old = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var item in old) item.Dispose();

        var loc = EffectiveLocation();
        string locText = locationProblem != null && settings.LastLocation == null
            ? "Approximate location (from time zone)"
            : $"Following the sun at {loc.lat:0.00}, {loc.lon:0.00}";
        menu.Items.Add(new ToolStripMenuItem(locText) { Enabled = false });
        if (locationProblem != null)
            menu.Items.Add(Item("Allow Location Access…", () =>
            {
                LocationProvider.OpenSettings();
                var t = new Debouncer(5000);       // try again in case the user just granted access
                t.Fire += () => _ = RefreshLocationAsync();
                t.Trigger();
            }));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Choice("Window", Options.Scenes, () => settings.Scene, v => settings.Scene = v));
        menu.Items.Add(Choice("Window Decoration", Options.Decorations, () => settings.Decoration, v => settings.Decoration = v));
        menu.Items.Add(Choice("Wall", Options.Walls, () => settings.Wall, v => settings.Wall = v));
        var dyn = Toggle("Dynamic Environment", settings.Dynamic, () => { settings.Dynamic = !settings.Dynamic; Changed(); });
        dyn.ToolTipText = "A gentle breeze through the plants, trees, curtain and blinds.";
        menu.Items.Add(dyn);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Toggle("Preview Day", previewDay, () => { if (previewDay) StopPreview(); else StartPreview(); }));
        menu.Items.Add(Toggle("Show Time", settings.ShowHUD, () => { settings.ShowHUD = !settings.ShowHUD; Changed(); }));
        menu.Items.Add(Toggle("Low Power Mode", settings.LowPower, () => { settings.LowPower = !settings.LowPower; Changed(); }));
        menu.Items.Add(Toggle("Launch at Login", LaunchAtLogin.IsEnabled, () =>
        {
            try { LaunchAtLogin.SetEnabled(!LaunchAtLogin.IsEnabled); }
            catch (Exception e) { MessageBox.Show($"Couldn't change Launch at Login: {e.Message}", "Lull"); }
        }));

        menu.Items.Add(new ToolStripSeparator());
        if (ScreenSaverInstall.IsActive)
            menu.Items.Add(Item("Screen Saver Settings…", ScreenSaverInstall.OpenSettings));
        else
        {
            var saver = Item("Use as Screen Saver…", () =>
            {
                try { ScreenSaverInstall.Install(); }
                catch (Exception e)
                {
                    Log.Write($"screen saver: install failed: {e}");
                    MessageBox.Show($"Couldn't set up the screen saver: {e.Message}", "Lull");
                }
            });
            saver.ToolTipText = "Makes Lull your screen saver, with the same look as your wallpaper.";
            menu.Items.Add(saver);
        }
        menu.Items.Add(Item("Reload Wallpaper", () => { gaveUp = false; failures = 0; recentResets.Clear(); _ = RebuildAsync(); }));
        menu.Items.Add(Item("Quit Lull", () => ExitThread()));
        menu.ResumeLayout();
    }

    ToolStripMenuItem Item(string title, Action action)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += (_, _) => action();
        return item;
    }

    ToolStripMenuItem Toggle(string title, bool on, Action action)
    {
        var item = Item(title, action);
        item.Checked = on;
        return item;
    }

    ToolStripMenuItem Choice(string title, (string title, string value)[] options, Func<string> get, Action<string> set)
    {
        var parent = new ToolStripMenuItem(title);
        foreach (var (t, v) in options)
        {
            var item = Item(t, () => { set(v); Changed(); });
            item.Checked = get() == v;
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    void RefreshTrayIcon()
    {
        var old = tray.Icon;
        tray.Icon = TrayIconArt.Make();
        old?.Dispose();
    }

    void ApplyMenuTheme() => menu.Renderer = MenuTheme.AppsUseDark() ? new MenuTheme.DarkRenderer() : new ToolStripProfessionalRenderer();

    void ShowBalloon(string title, string text)
    {
        tray.BalloonTipTitle = title;
        tray.BalloonTipText = text;
        tray.ShowBalloonTip(8000);
    }

    // MARK: Preview Day

    /// Unwrapped preview time in minutes since local midnight (may exceed 1440 until the timer wraps it).
    double PreviewMinutesNow()
    {
        double elapsed = previewPlaying ? (DateTime.Now - previewSetAt).TotalSeconds * PreviewSpeed / 60.0 : 0;
        return previewBase + elapsed;
    }

    void StartPreview()
    {
        var now = DateTime.Now;
        previewBase = now.Hour * 60 + now.Minute + now.Second / 60.0;
        previewSetAt = now;
        previewPlaying = true;
        previewDay = true;

        if (previewForm == null)
        {
            previewForm = new PreviewForm();
            previewForm.Scrub += minutes =>
            {
                previewBase = minutes;
                previewSetAt = DateTime.Now;
                SchedulePreviewPush();
            };
            previewForm.TogglePlay += () =>
            {
                previewBase = PreviewMinutesNow() % 1440;
                previewSetAt = DateTime.Now;
                previewPlaying = !previewPlaying;
                previewForm?.SetPlaying(previewPlaying);
                PushConfig();
            };
            previewForm.Done += StopPreview;
        }
        previewForm.SetPlaying(true);
        previewForm.SetMinutes(previewBase);
        previewForm.Show();
        previewForm.PositionOnPrimaryScreen();
        previewForm.Activate();

        previewTimer.Start();
        PushConfig();
    }

    /// Slider events can arrive very quickly; coalesce to at most ~30 pushes per second.
    void SchedulePreviewPush()
    {
        if (scrubPushPending) return;
        scrubPushPending = true;
        var t = new Debouncer(33);
        t.Fire += () => { scrubPushPending = false; PushConfig(); };
        t.Trigger();
    }

    void PreviewTick()
    {
        if (!previewDay) return;
        double raw = PreviewMinutesNow();
        if (raw >= 1440)
        {
            // Loop the day: jump the scene back to just after midnight.
            previewBase = raw % 1440;
            previewSetAt = DateTime.Now;
            PushConfig();
        }
        previewForm?.SetMinutes(raw % 1440);
    }

    void StopPreview()
    {
        previewDay = false;
        previewTimer.Stop();
        previewForm?.Hide();
        PushConfig();
    }

    // MARK: Shutdown

    bool cleanedUp;

    void Cleanup()
    {
        if (cleanedUp) return;
        cleanedUp = true;
        coverageTimer.Stop();
        previewTimer.Stop();
        locationTimer.Stop();
        coverageEvents?.Dispose();
        var d = desktop;
        TearDownHosts();
        Desktop.RefreshWallpaper(d);       // classic layout only: repaint the normal wallpaper
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        messages.Dispose();
        tray.Visible = false;
        tray.Dispose();
    }

    protected override void ExitThreadCore()
    {
        Cleanup();
        base.ExitThreadCore();
    }

    /// Hidden *top-level* window (message-only windows don't get the broadcasts below):
    /// Explorer restarts, display changes, display power and suspend/resume.
    sealed class MessageWindow : NativeWindow, IDisposable
    {
        readonly LullApp app;
        readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        IntPtr powerNotify;

        public MessageWindow(LullApp app)
        {
            this.app = app;
            CreateHandle(new CreateParams
            {
                Caption = "Lull Messages",
                Style = unchecked((int)WS_POPUP),
                ExStyle = (int)WS_EX_TOOLWINDOW,
                X = -32000, Y = -32000, Width = 1, Height = 1,
            });
            var guid = GUID_CONSOLE_DISPLAY_STATE;
            powerNotify = RegisterPowerSettingNotification(Handle, ref guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        }

        protected override void WndProc(ref Message m)
        {
            if (taskbarCreated != 0 && m.Msg == (int)taskbarCreated)
            {
                app.ExplorerRestarted();
            }
            else switch (m.Msg)
            {
                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                    app.DisplaysMaybeChanged();
                    break;
                case WM_POWERBROADCAST:
                    switch (m.WParam.ToInt32())
                    {
                        case PBT_POWERSETTINGCHANGE:
                            // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; BYTE Data[] }
                            var setting = Marshal.PtrToStructure<Guid>(m.LParam);
                            if (setting == GUID_CONSOLE_DISPLAY_STATE)
                                app.DisplayPower(Marshal.ReadInt32(m.LParam, 20) != 0);   // 0 off, 1 on, 2 dimmed
                            break;
                        case PBT_APMSUSPEND:
                            app.Suspending();
                            break;
                        case PBT_APMRESUMEAUTOMATIC:   // always sent on resume (RESUMESUSPEND follows it if a user is present)
                            app.Resumed();
                            break;
                    }
                    m.Result = new IntPtr(1);
                    return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (powerNotify != IntPtr.Zero) UnregisterPowerSettingNotification(powerNotify);
            powerNotify = IntPtr.Zero;
            DestroyHandle();
        }
    }
}

/// One-shot, restartable delay on the UI thread.
internal sealed class Debouncer
{
    readonly Timer timer = new();
    readonly int defaultMs;
    public event Action? Fire;

    public Debouncer(int ms)
    {
        defaultMs = ms;
        timer.Tick += (_, _) => { timer.Stop(); Fire?.Invoke(); };
    }

    public bool Pending => timer.Enabled;

    public void Trigger(int? ms = null)
    {
        timer.Stop();
        timer.Interval = Math.Max(1, ms ?? defaultMs);
        timer.Start();
    }
}

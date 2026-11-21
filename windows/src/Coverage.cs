using System;
using System.Collections.Generic;
using static Lull.Native;

namespace Lull;

/// Works out which monitors have their desktop fully covered by other windows, so the scene on that
/// monitor can stop drawing (the Mac app gets this from NSWindow occlusion; Windows has no equivalent).
///
/// A monitor counts as covered when a real, visible app window on it is maximized, or covers at least
/// 95% of its work area. Heuristic modelled on what Lively Wallpaper does.
internal static class Coverage
{
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",            // Start, search, notification center
        "XamlExplorerHostIslandWindow",          // Task view / Alt-Tab on Windows 11
        "MultitaskingViewFrame", "ForegroundStaging", "TopLevelWindowForOverflowXamlIsland",
    };

    /// Returns one flag per monitor, in the same order.
    public static bool[] Compute(Display[] monitors, uint ownPid)
    {
        var covered = new bool[monitors.Length];
        int remaining = monitors.Length;
        EnumWindows((hwnd, _) =>
        {
            if (remaining == 0) return false;
            if (!Counts(hwnd, ownPid)) return true;
            if (!GetWindowRect(hwnd, out RECT r)) return true;
            bool zoomed = IsZoomed(hwnd);
            for (int i = 0; i < monitors.Length; i++)
            {
                if (covered[i]) continue;
                var m = monitors[i];
                bool hit;
                if (zoomed)
                {
                    // A maximized window belongs to the monitor that holds most of it.
                    hit = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == m.Handle;
                }
                else
                {
                    long area = r.Intersect(m.Work).Area;
                    hit = m.Work.Area > 0 && area >= 0.95 * m.Work.Area;
                }
                if (hit) { covered[i] = true; remaining--; }
            }
            return true;
        }, IntPtr.Zero);
        return covered;
    }

    /// Only real top-level app windows the user can see.
    static bool Counts(IntPtr hwnd, uint ownPid)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return false;
        if (IsCloaked(hwnd)) return false;                   // suspended UWP apps, other virtual desktops
        long ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if ((ex & WS_EX_LAYERED) != 0 && (ex & WS_EX_TRANSPARENT) != 0) return false;   // click-through overlays
        if ((ex & WS_EX_NOACTIVATE) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        if (ProcessOf(hwnd) == ownPid) return false;          // our own preview bar etc.
        return !ShellClasses.Contains(ClassOf(hwnd));
    }
}

/// Fires `changed` (on the UI thread) when something that could change coverage happens: foreground
/// changes, minimize/restore, windows cloaking. Window moves/resizes are caught by the caller's poll.
internal sealed class CoverageEvents : IDisposable
{
    readonly WinEventProc proc;        // must stay referenced while the hooks exist
    readonly List<IntPtr> hooks = new();

    public CoverageEvents(Action changed)
    {
        proc = (_, _, _, idObject, idChild, _, _) =>
        {
            if (idObject == OBJID_WINDOW && idChild == 0) changed();
        };
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
    }

    void Hook(uint min, uint max)
    {
        IntPtr h = SetWinEventHook(min, max, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) hooks.Add(h);
    }

    public void Dispose()
    {
        foreach (var h in hooks) UnhookWinEvent(h);
        hooks.Clear();
    }
}

/// Fires when the WorkerW we depend on is destroyed (Explorer does this on wallpaper / slideshow
/// changes, theme changes and sometimes on unlock).
internal sealed class WorkerWWatcher : IDisposable
{
    readonly WinEventProc proc;
    IntPtr hook;

    public WorkerWWatcher(Desktop.Handles h, Action destroyed)
    {
        IntPtr watched = h.WorkerW;
        proc = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject == OBJID_WINDOW && hwnd == watched) destroyed();
        };
        uint thread = h.WorkerWThread;
        if (thread != 0)
            hook = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY, IntPtr.Zero, proc,
                                   h.ExplorerPid, thread, WINEVENT_OUTOFCONTEXT);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        hook = IntPtr.Zero;
    }
}

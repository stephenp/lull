using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using static Lull.Native;

namespace Lull;

/// Puts our windows behind the desktop icons. Windows has no supported API for this: like every live
/// wallpaper app, Lull asks Explorer to "raise" the desktop (undocumented message 0x052C) and then
/// parents its windows into Explorer's desktop window tree. Explorer has two layouts:
///
///   Classic (Windows 10, Windows 11 up to 23H2):
///     WorkerW (top-level)  -> SHELLDLL_DefView -> SysListView32 (icons)
///     WorkerW (top-level)  <- empty, right behind the icons: we parent here
///     Progman
///
///   Raised (Windows 11 24H2 with the new shell background, 25H2):
///     Progman (top-level, WS_EX_NOREDIRECTIONBITMAP)
///       SHELLDLL_DefView (WS_EX_LAYERED, icons)
///       [our window]     (WS_EX_LAYERED child, alpha 255): we parent to Progman, just below DefView
///       WorkerW          (Explorer's own wallpaper, must stay at the bottom)
///
/// The layout is detected from Progman's extended style, not from the Windows build number.
/// Approach learned from reading Lively Wallpaper (GPL-3, github.com/rocksdanister/lively); no code copied.
internal static class Desktop
{
    public enum Layout { Classic, Raised }

    public sealed record Handles(Layout Layout, IntPtr Progman, IntPtr ShellView, IntPtr WorkerW, IntPtr Parent)
    {
        public bool StillValid => IsWindow(Progman) && IsWindow(Parent) && (WorkerW == IntPtr.Zero || IsWindow(WorkerW));
        public uint ExplorerPid => ProcessOf(Progman);
        public uint WorkerWThread => WorkerW == IntPtr.Zero ? 0 : GetWindowThreadProcessId(WorkerW, out _);
    }

    /// Finds (and if needed creates) the window we should parent into. Returns null if Explorer isn't
    /// running or hasn't finished creating its windows yet; the caller retries.
    /// `forceWorkerWParent` is the escape hatch for the raised layout (parent to the child WorkerW
    /// instead of Progman), for machines where the default doesn't show anything.
    public static Handles? Discover(bool forceWorkerWParent)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var h = TryDiscover(forceWorkerWParent);
            if (h != null) return h;
            Thread.Sleep(500);
        }
        return null;
    }

    static Handles? TryDiscover(bool forceWorkerWParent)
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return null;

        // Parameters (0xD, 1) are what current live wallpaper apps send; older samples send (0, 0).
        SendMessageTimeout(progman, WM_SPAWN_WORKER, new IntPtr(0xD), new IntPtr(1), SMTO_NORMAL, 1000, out _);

        bool raised = (GetWindowLong(progman, GWL_EXSTYLE) & WS_EX_NOREDIRECTIONBITMAP) != 0;
        if (raised)
        {
            IntPtr shell = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr worker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            if (shell == IntPtr.Zero || worker == IntPtr.Zero) return null;
            return new Handles(Layout.Raised, progman, shell, worker, forceWorkerWParent ? worker : progman);
        }

        IntPtr shellView = IntPtr.Zero, target = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            IntPtr sv = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (sv != IntPtr.Zero)
            {
                shellView = sv;
                // The WorkerW that comes *after* the one holding the icons in z-order (behind it).
                target = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero) return null;
        return new Handles(Layout.Classic, progman, shellView, target, target);
    }

    /// Turns `hwnd` (a top-level WS_POPUP tool window) into a child of the desktop, covering `monitor`.
    public static bool Attach(IntPtr hwnd, Handles h, Display monitor)
    {
        long style = GetWindowLong(hwnd, GWL_STYLE);
        style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
        SetWindowLong(hwnd, GWL_STYLE, style);

        if (h.Layout == Layout.Raised)
        {
            // Must be layered *before* SetParent, or the style doesn't take.
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        }

        if (SetParent(hwnd, h.Parent) == IntPtr.Zero)
        {
            Log.Write($"desktop: SetParent failed (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return false;
        }

        Position(hwnd, h, monitor, frameChanged: true);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        Reinsert(hwnd, h);
        return true;
    }

    /// Raised layout: directly below the icons, then make sure Explorer's own wallpaper WorkerW is
    /// still underneath us. (Classic layout: nothing to do, the WorkerW is ours alone.)
    public static void Reinsert(IntPtr hwnd, Handles h)
    {
        if (h.Layout != Layout.Raised || h.Parent != h.Progman) return;
        SetWindowPos(hwnd, h.ShellView, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        EnsureWorkerWAtBottom(h);
    }

    /// Places the child over `monitor`. Child coordinates are relative to the parent's client area,
    /// which spans the virtual screen (its origin can be negative in screen space), so map against
    /// the window we're actually parented to. Never changes z-order.
    public static void Position(IntPtr hwnd, Handles h, Display monitor, bool frameChanged = false)
    {
        var pt = new POINT(monitor.Bounds.Left, monitor.Bounds.Top);
        MapWindowPoints(IntPtr.Zero, h.Parent, ref pt, 1);
        uint flags = SWP_NOZORDER | SWP_NOACTIVATE | (frameChanged ? SWP_FRAMECHANGED : 0);
        SetWindowPos(hwnd, IntPtr.Zero, pt.X, pt.Y, monitor.Bounds.Width, monitor.Bounds.Height, flags);
    }

    public static void EnsureWorkerWAtBottom(Handles h)
    {
        if (h.Layout != Layout.Raised || h.Parent != h.Progman || !IsWindow(h.WorkerW)) return;
        IntPtr last = IntPtr.Zero;
        EnumChildWindows(h.Progman, (c, _) => { if (GetParent(c) == h.Progman) last = c; return true; }, IntPtr.Zero);
        if (last != h.WorkerW)
            SetWindowPos(h.WorkerW, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// True if our window is still where we put it (Explorer can destroy or reshuffle its windows).
    public static bool IsStillAttached(IntPtr hwnd, Handles h) =>
        h.StillValid && IsWindow(hwnd) && GetParent(hwnd) == h.Parent;

    /// Undo the attach before destroying our window, so the desktop doesn't keep a dead surface.
    public static void Detach(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;
        ShowWindow(hwnd, SW_HIDE);
        SetParent(hwnd, IntPtr.Zero);
    }

    /// After all wallpapers are gone. Only safe in the classic layout: in the raised layout this
    /// destroys Explorer's wallpaper WorkerW.
    public static void RefreshWallpaper(Handles? h)
    {
        if (h?.Layout != Layout.Classic) return;
        // Re-apply the user's current wallpaper so Explorer repaints over our last frame. Passing NULL
        // would revert to the default wallpaper, and we don't persist anything (no SPIF_UPDATEINIFILE).
        var path = new StringBuilder(MAX_PATH);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0) && path.Length > 0)
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path.ToString(), 0);
    }

    /// One-line-per-window dump of Explorer's desktop tree, written to the log for bug reports.
    public static string Describe(Handles? h)
    {
        var sb = new StringBuilder();
        sb.Append($"layout={h?.Layout.ToString() ?? "none"} ");
        IntPtr progman = FindWindow("Progman", null);
        sb.Append($"progman={progman:X} exstyle={GetWindowLong(progman, GWL_EXSTYLE):X}");
        var lines = new List<string>();
        void Add(IntPtr w, string indent) =>
            lines.Add($"{indent}{ClassOf(w)} {w:X} parent={GetParent(w):X} ex={GetWindowLong(w, GWL_EXSTYLE):X} vis={IsWindowVisible(w)}");
        EnumWindows((top, _) =>
        {
            string cls = ClassOf(top);
            if (cls == "Progman" || cls == "WorkerW")
            {
                Add(top, "  ");
                EnumChildWindows(top, (c, _) => { if (GetParent(c) == top) Add(c, "    "); return true; }, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
        foreach (var l in lines) sb.Append('\n').Append(l);
        return sb.ToString();
    }
}

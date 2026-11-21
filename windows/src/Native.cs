using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Lull;

/// Win32 P/Invoke declarations. Everything Lull needs from user32 / dwmapi / shcore lives here.
internal static class Native
{
    // ---- messages ----
    public const int WM_DESTROY = 0x0002;
    public const int WM_SIZE = 0x0005;
    public const int WM_ERASEBKGND = 0x0014;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_WTSSESSION_CHANGE = 0x02B1;
    public const int MA_NOACTIVATE = 3;
    public const int HTTRANSPARENT = -1;
    public const uint WM_SPAWN_WORKER = 0x052C;   // undocumented: asks Explorer to "raise" the desktop

    public const int PBT_APMSUSPEND = 0x0004;
    public const int PBT_APMRESUMESUSPEND = 0x0007;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    // ---- styles ----
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_POPUP = 0x80000000L;
    public const long WS_VISIBLE = 0x10000000L;
    public const long WS_CLIPCHILDREN = 0x02000000L;
    public const long WS_CLIPSIBLINGS = 0x04000000L;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const uint LWA_ALPHA = 0x2;

    // ---- SetWindowPos ----
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                      SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    public const uint SMTO_NORMAL = 0x0000;
    public const uint SPI_SETDESKWALLPAPER = 0x0014, SPI_GETDESKWALLPAPER = 0x0073, SPIF_UPDATEINIFILE = 0x01;
    public const int MAX_PATH = 260;

    // ---- WinEvents ----
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017;
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;

    public const int DWMWA_CLOAKED = 14;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;
    public const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly long Area => Width <= 0 || Height <= 0 ? 0 : (long)Width * Height;
        public readonly RECT Intersect(RECT o) => new(
            Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
        public override readonly string ToString() => $"({Left},{Top} {Width}x{Height})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT pt, uint count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, string? pvParam, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder pvParam, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, int flags);
    [DllImport("user32.dll")] public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    public static long GetWindowLong(IntPtr hwnd, int index) => GetWindowLongPtr64(hwnd, index).ToInt64();
    public static void SetWindowLong(IntPtr hwnd, int index, long value) => SetWindowLongPtr64(hwnd, index, new IntPtr(value));

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc,
        uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    public static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static uint ProcessOf(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return pid; }

    /// A monitor in physical pixels (the process is Per-Display-V2 DPI aware).
    public readonly record struct Display(IntPtr Handle, RECT Bounds, RECT Work, uint Dpi, string Device)
    {
        public double Scale => Dpi / 96.0;
        public string Signature => $"{Device}:{Bounds}:{Dpi}";
    }

    public static Display[] GetDisplays()
    {
        var list = new System.Collections.Generic.List<Display>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (GetMonitorInfo(h, ref info))
            {
                uint dpi = GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0 ? dx : 96;
                list.Add(new Display(h, info.rcMonitor, info.rcWork, dpi, info.szDevice));
            }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
}

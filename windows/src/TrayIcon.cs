using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Lull;

/// Lull's tray icon: the same two slanted slats as the Mac menu bar icon, drawn in code so it is
/// crisp at the tray's actual size. White on a dark taskbar, near-black on a light one.
public static class TrayIconArt
{
    public static Icon Make()
    {
        // The tray uses SM_CXSMICON (16 px at 100%, 20 at 125%, 24 at 150%, 32 at 200%).
        int size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        bool lightTaskbar = TaskbarIsLight();
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(lightTaskbar ? Color.FromArgb(255, 28, 28, 28) : Color.White);
            float s = size / 18f;
            // Coordinates from mac/Sources/MenuBarIcon.swift, flipped vertically (AppKit's origin is bottom-left).
            PointF P(float x, float y) => new(x * s, (18 - y) * s);
            g.FillPolygon(brush, new[] { P(7.4f, 15.5f), P(10.0f, 15.5f), P(6.9f, 2.5f), P(4.3f, 2.5f) });
            g.FillPolygon(brush, new[] { P(13.2f, 12.0f), P(15.8f, 12.0f), P(13.5f, 2.5f), P(10.9f, 2.5f) });
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    static bool TaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    const int SM_CXSMICON = 49;
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
}

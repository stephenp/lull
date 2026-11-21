using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Lull;

/// Dark tray menu when Windows is in dark app mode (WinForms menus are always light otherwise).
internal static class MenuTheme
{
    public static bool AppsUseDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        static readonly Color Back = Color.FromArgb(43, 43, 43);
        static readonly Color Hover = Color.FromArgb(61, 61, 61);
        static readonly Color Text = Color.FromArgb(242, 242, 242);
        static readonly Color Disabled = Color.FromArgb(140, 140, 140);
        static readonly Color Line = Color.FromArgb(70, 70, 70);

        public DarkRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A plain check mark instead of the highlighted box.
            var r = e.ImageRectangle;
            using var pen = new Pen(Text, 1.6f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float x = r.Left + r.Width * 0.22f, y = r.Top + r.Height * 0.52f;
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(x, y),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.30f),
            });
        }

        sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Back;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Line;
            public override Color CheckBackground => Back;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Lull;

/// Floating control bar shown during Preview Day: play/pause, a 24-hour scrubber, the time, and Done.
/// Mirrors mac/Sources/PreviewPanel.swift. The wallpaper itself is click-through, so scrubbing happens here.
public sealed class PreviewForm : Form
{
    public event Action<double>? Scrub;      // minutes since local midnight (0..<1440)
    public event Action? TogglePlay;
    public event Action? Done;

    readonly TrackBar slider = new();
    readonly Label timeLabel = new();
    readonly Button playButton = new();
    readonly Button doneButton = new();
    bool scrubbing;
    bool settingValue;   // true while we move the slider ourselves

    public PreviewForm()
    {
        Text = "Lull: Preview Day";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);
        Padding = new Padding(10, 6, 10, 6);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Segoe Fluent Icons (Win11) / Segoe MDL2 Assets (Win10) play/pause glyphs.
        playButton.Font = new Font(FontFamilyExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 11f);
        playButton.FlatStyle = FlatStyle.Flat;
        playButton.FlatAppearance.BorderSize = 0;
        playButton.Size = new Size(36, 32);
        playButton.Anchor = AnchorStyles.None;
        playButton.Click += (_, _) => TogglePlay?.Invoke();

        slider.Minimum = 0;
        slider.Maximum = 1439;
        slider.TickFrequency = 60;
        slider.SmallChange = 15;
        slider.LargeChange = 60;
        slider.TickStyle = TickStyle.BottomRight;
        slider.Dock = DockStyle.Fill;
        slider.MinimumSize = new Size(380, 0);
        slider.MouseDown += (_, _) => scrubbing = true;
        slider.MouseUp += (_, _) => scrubbing = false;
        slider.ValueChanged += (_, _) =>
        {
            UpdateLabel(slider.Value);
            if (!settingValue) Scrub?.Invoke(slider.Value);   // mouse drag or arrow keys
        };

        timeLabel.AutoSize = false;
        timeLabel.Size = new Size(52, 32);
        timeLabel.TextAlign = ContentAlignment.MiddleRight;
        timeLabel.Font = new Font("Segoe UI Semibold", 10f);
        timeLabel.Anchor = AnchorStyles.None;

        doneButton.Text = "Done";
        doneButton.FlatStyle = FlatStyle.System;
        doneButton.AutoSize = true;
        doneButton.Anchor = AnchorStyles.None;
        doneButton.Click += (_, _) => Done?.Invoke();

        layout.Controls.Add(playButton, 0, 0);
        layout.Controls.Add(slider, 1, 0);
        layout.Controls.Add(timeLabel, 2, 0);
        layout.Controls.Add(doneButton, 3, 0);
        Controls.Add(layout);

        ClientSize = new Size(620, 56);
        SetPlaying(true);
    }

    /// Closing the window ends the preview rather than destroying the form.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Done?.Invoke();
            return;
        }
        base.OnFormClosing(e);
    }

    /// Bottom-centre of the primary screen's work area, just above the taskbar.
    public void PositionOnPrimaryScreen()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 28);
    }

    public void SetMinutes(double minutes)
    {
        if (scrubbing) return;
        int m = Math.Clamp((int)Math.Floor(minutes) % 1440, 0, 1439);
        settingValue = true;
        if (slider.Value != m) slider.Value = m;
        settingValue = false;
        UpdateLabel(m);
    }

    public void SetPlaying(bool playing)
    {
        playButton.Text = playing ? "" : ""; // Pause / Play
        playButton.AccessibleName = playing ? "Pause" : "Play";
    }

    void UpdateLabel(int minutes)
    {
        int m = ((minutes % 1440) + 1440) % 1440;
        timeLabel.Text = $"{m / 60:00}:{m % 60:00}";
    }

    static bool FontFamilyExists(string name)
    {
        using var f = new Font(name, 10f);
        return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
    }
}

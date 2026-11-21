using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lull;

/// Persisted user settings, stored as JSON in %APPDATA%\Lull\settings.json.
/// Mirrors mac/Sources/Settings.swift (same keys and defaults).
public sealed class Settings
{
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string Scene { get; set; } = "blinds";
    public string Decoration { get; set; } = "foliage";
    public string Wall { get; set; } = "knockdown";
    public bool Dynamic { get; set; } = true;
    public bool ShowHUD { get; set; } = false;
    public bool LowPower { get; set; } = false;
    /// Escape hatch if covered-desktop detection ever misbehaves (edit settings.json by hand).
    public bool PauseWhenCovered { get; set; } = true;
    /// Escape hatch for Windows 11 24H2+ ("raised" desktop): parent to Explorer's WorkerW instead of
    /// Progman, for machines where the wallpaper doesn't show up by default. Hand-edit to enable.
    public bool ParentToWorkerW { get; set; } = false;
    /// Extra Chromium flags for WebView2, for troubleshooting (e.g. "--disable-features=CalculateNativeWinOcclusion").
    public string BrowserArguments { get; set; } = "";

    [JsonIgnore]
    public (double lat, double lon)? LastLocation
    {
        get => Lat is double a && Lon is double b ? (a, b) : null;
        set { Lat = value?.lat; Lon = value?.lon; }
    }

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lull");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json);
                if (s != null) { s.Normalize(); return s; }
            }
        }
        catch (Exception e)
        {
            Log.Write($"settings: couldn't read {FilePath}: {e.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Write($"settings: couldn't save: {e.Message}");
        }
    }

    /// Guard against hand-edited or stale values.
    void Normalize()
    {
        if (Array.IndexOf(Options.SceneValues, Scene) < 0) Scene = "blinds";
        if (Array.IndexOf(Options.DecorationValues, Decoration) < 0) Decoration = "foliage";
        if (Array.IndexOf(Options.WallValues, Wall) < 0) Wall = "knockdown";
    }
}

public static class Options
{
    public static readonly (string title, string value)[] Scenes =
        { ("Blinds", "blinds"), ("Window Frame", "window"), ("Arched Window", "arch") };
    public static readonly (string title, string value)[] Decorations =
        { ("None", "none"), ("Foliage (Tree Outside)", "foliage"), ("Sheer Curtain", "curtain") };
    public static readonly (string title, string value)[] Walls =
        { ("Knockdown", "knockdown"), ("Limewash", "limewash"), ("Oak Panels", "wood") };

    public static readonly string[] SceneValues = Array.ConvertAll(Scenes, x => x.value);
    public static readonly string[] DecorationValues = Array.ConvertAll(Decorations, x => x.value);
    public static readonly string[] WallValues = Array.ConvertAll(Walls, x => x.value);
}

/// Tiny rolling log at %LOCALAPPDATA%\Lull\lull.log (the Windows stand-in for NSLog).
public static class Log
{
    static readonly object Gate = new();
    static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lull", "lull.log");

    public static void Write(string message)
    {
        System.Diagnostics.Debug.WriteLine("Lull: " + message);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
                var fi = new FileInfo(Path_);
                if (fi.Exists && fi.Length > 512 * 1024) File.Delete(Path_);
                File.AppendAllText(Path_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }
}

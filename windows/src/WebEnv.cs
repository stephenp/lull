using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Lull;

/// What every WebView2 host in Lull needs: the scene unpacked to a folder, and a browser environment.
internal static class WebEnv
{
    static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lull");

    /// The scene ships inside Lull.exe; unpack it so WebView2 can serve it from a folder.
    /// For development, LULL_WEB_DIR (or a web\index.html next to the exe) overrides it.
    public static string PrepareWebFolder()
    {
        var overrideDir = Environment.GetEnvironmentVariable("LULL_WEB_DIR");
        if (!string.IsNullOrEmpty(overrideDir) && File.Exists(Path.Combine(overrideDir, "index.html"))) return overrideDir;
        var beside = Path.Combine(AppContext.BaseDirectory, "web");
        if (File.Exists(Path.Combine(beside, "index.html"))) return beside;

        var dir = Path.Combine(LocalDir, "web");
        Directory.CreateDirectory(dir);
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lull.web.index.html")
                        ?? throw new InvalidOperationException("embedded web/index.html is missing from the build");
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var bytes = ms.ToArray();
        var target = Path.Combine(dir, "index.html");
        if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(target, bytes);
        return dir;
    }

    /// Creates a WebView2 environment with its data in %LOCALAPPDATA%\Lull\<dataFolderName>. Processes that
    /// share a data folder must use identical options, so the screen saver keeps a folder of its own.
    /// Throws WebView2RuntimeNotFoundException if the runtime is missing.
    public static Task<CoreWebView2Environment> CreateAsync(string dataFolderName, string browserArguments)
    {
        var options = new CoreWebView2EnvironmentOptions(
            string.IsNullOrWhiteSpace(browserArguments) ? null : browserArguments);
        return CoreWebView2Environment.CreateAsync(null, Path.Combine(LocalDir, dataFolderName), options);
    }
}

# Lull for Windows

> **Lightly tested.** This port was written with Claude. It runs well on my Windows PC, but that's
> the only machine it has been tried on so far, so expect rough edges on other setups, especially
> in the part that puts the wallpaper behind the desktop icons.

The Windows version is a thin native shell around the same scene as the Mac app (`../web/index.html`).
It's a C# (.NET 10) tray app that hosts the scene in Microsoft's WebView2 (the Edge/Chromium web view
built into Windows), one window per monitor, behind the desktop icons.

## How it maps to the Mac app

| Mac (`mac/Sources`) | Windows (`windows/src`) |
|---|---|
| `WallpaperWindow` (desktop-level `NSWindow` + `WKWebView`) | `WallpaperHost` (child of Explorer's desktop window + `CoreWebView2Controller`) |
| `level = desktopWindow` | `Desktop` (undocumented Progman/WorkerW parenting, see below) |
| Occlusion notifications | `Coverage` (pauses a monitor when a maximized or near-fullscreen window covers it) |
| Screens sleep/wake | `WM_POWERBROADCAST` display state, suspend/resume, session lock |
| Menu bar icon + menu | Tray icon + menu (`TrayIcon`, `MenuTheme`), same options |
| `PreviewPanel` | `PreviewForm` |
| CoreLocation | `LocationProvider` (Windows location service), same time-zone fallback |
| `SMAppService` login item | `LaunchAtLogin` (`HKCU\...\Run`) |
| `UserDefaults` | `%APPDATA%\Lull\settings.json` |
| `Lull.saver` (separate plug-in bundle, `mac/Saver`) | The same `Lull.exe`, copied to `Lull.scr` (`ScreenSaverApp`, `ScreenSaverInstall`) |

## Screen saver

A Windows screen saver is an ordinary exe renamed `.scr`. Windows starts it with `/s` (run full screen),
`/p <hwnd>` (draw into the preview box in Screen Saver Settings) or `/c` (the Settings button). So there's no
second binary: `Program.Main` checks for those arguments before starting the tray app.

- **Use as Screen Saver…** in the tray menu copies `Lull.exe` to `%LOCALAPPDATA%\Lull\Lull.scr`, points
  `HKCU\Control Panel\Desktop\SCRNSAVE.EXE` at it, and opens Screen Saver Settings. No admin rights needed.
  On every launch the tray app refreshes `Lull.scr` if it came from a different build.
- `/s` opens a topmost window per monitor, using the same `SceneView` (WebView2 host) as the wallpaper.
  WebView2 swallows mouse and keyboard input, so the screen saver ends by watching the system instead:
  the cursor moving more than 10 px, or any other input (`GetLastInputInfo`).
- It reads `settings.json` but never writes it, and never asks for location (it uses the last location the
  app saved, or the time-zone estimate). It has its own WebView2 data folder (`WebView2-saver`), so it can
  run next to the wallpaper even with different `BrowserArguments`. The running wallpaper pauses under it,
  because `Coverage` sees a fullscreen window.
- `/c` explains that the screen saver follows the tray menu, and offers to start Lull if it isn't running.

To try it without installing: `Lull.exe /s`.

## Behind the desktop icons

Windows has no supported way to put a window behind the icons, so Lull does what every live
wallpaper app does. It sends Explorer an undocumented message (`0x052C`) that splits the wallpaper
layer from the icon layer, then parents its windows into that layer. Explorer has two layouts, and
`Desktop.cs` detects which one from Progman's window style:

- **Classic** (Windows 10, Windows 11 up to 23H2): parent to the empty `WorkerW` behind the icons.
- **Raised** (Windows 11 24H2 with the new shell background, and 25H2): parent to `Progman` as a
  layered child, z-ordered just under the icons (`SHELLDLL_DefView`) and above Explorer's own
  wallpaper `WorkerW`.

Lull rebuilds its windows when Explorer restarts, when its `WorkerW` is destroyed, and when
monitors change. A health check every 5 seconds covers anything those events miss. The approach
came from reading [Lively Wallpaper](https://github.com/rocksdanister/lively) (GPL-3; no code
copied).

Because this relies on Explorer internals, a Windows update can break it, as 24H2 did for every
wallpaper app.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). On Windows:

```powershell
cd windows
dotnet publish -c Release -o out
.\out\Lull.exe
```

That produces a single self-contained `Lull.exe` (no .NET install needed to run it). The scene is
embedded in the exe, so after editing `web/src.html`, run `python3 web/build.py` first.

The GitHub Actions workflow (`.github/workflows/windows.yml`) builds the same exe on every push and
uploads `Lull-windows.zip` as an artifact. `EnableWindowsTargeting` should also let you cross-build
from a Mac with the same command, but I haven't tried that.

For development, `LULL_WEB_DIR=C:\path\to\lull\web` makes Lull load `index.html` from that folder
instead of the embedded copy, and `LULL_DEVTOOLS=1` enables the WebView2 dev tools.

## Troubleshooting

- Log: `%LOCALAPPDATA%\Lull\lull.log`. On every (re)attach it logs Explorer's desktop window tree,
  which is the first thing to look at if the wallpaper doesn't show up.
- Hidden settings in `%APPDATA%\Lull\settings.json` (quit Lull before editing):
  - `"ParentToWorkerW": true`: on 24H2+, parent to Explorer's `WorkerW` instead of `Progman`. Try
    this if the wallpaper is invisible.
  - `"PauseWhenCovered": false`: never pause for covering windows.
  - `"BrowserArguments": "..."`: extra Chromium flags for WebView2, e.g.
    `--disable-features=CalculateNativeWinOcclusion` if the scene stops animating.

## Not tested yet

Beyond my own PC, in particular:

- Both desktop layouts (a Windows 10 or 23H2 machine and a 24H2/25H2 machine)
- Multiple monitors, mixed scaling, a monitor to the left of / above the primary
- Explorer restart (Task Manager → Windows Explorer → Restart), lock/unlock, sleep, display off
- Pausing under maximized windows and fullscreen games, and resuming after Win+D
- Location permission prompt on 24H2+, and the time-zone fallback
- GPU vendors (WebView2 inside a layered child window is what 24H2 requires; Intel / AMD / NVIDIA may differ)
- The screen saver: `/s` on multiple monitors with mixed scaling, the preview box in Screen Saver Settings
  (WebView2 inside another process's window), "On resume, display logon screen", and the wallpaper pausing
  underneath

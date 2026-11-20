<p align="center"><img src="docs/icon.png" width="128" alt="Lull app icon"></p>

<h1 align="center">Lull</h1>

<p align="center">A live Mac wallpaper of sunlight falling across a wall.<br>The light follows the real sun where you are, from first light to moonlight.</p>

![Late-afternoon light through an arched window and a tree, falling on oak panels](docs/screenshots/hero.jpg)

| | |
|---|---|
| ![Evening light through blinds and leaves on a knockdown wall](docs/screenshots/blinds.jpg) | ![Golden hour through a sheer curtain on limewash](docs/screenshots/curtain.jpg) |
| ![Moonlight through blinds at night, with the time display](docs/screenshots/night.jpg) | |

Lull is a small, native menu bar app. It works out the sun's position for your location (the NOAA solar algorithm) and renders the light coming through a window onto your wall in real time:

- **Light that moves with the day:** the light's height, color and timing follow the real sun. Cool skylight fills the shade, golden hour turns amber, and after dusk a cool moonlight takes over.
- **Windows:** blinds, a window frame, or a tall arched window.
- **Decorations:** a tree outside with dappled, layered foliage, or a sheer curtain.
- **Walls:** knockdown, limewash, or honey-oak panels.
- **Dynamic Environment:** a gentle breeze through the leaves, curtain and blinds.
- **Show Time:** the time, the date, time until sunset/sunrise and today's sun curve, drifting slowly to avoid burn-in.
- **Preview Day:** scrub through a whole day of light.
- **Light on resources:** it pauses completely while your desktop is covered or the displays sleep.

It started as a personal recreation of [sunstill.app](https://sunstill.app/).

> **Built with Claude.** The scene, the Mac app and this README were written with Claude (Anthropic). The Swift side was written without a Swift compiler in the loop, so if a build ever fails, the error from `./build.sh` should point straight at the problem.

## Requirements

- macOS 13 Ventura or later, on Apple Silicon or Intel
- To build: the Xcode Command Line Tools (`xcode-select --install`). Full Xcode works too but isn't needed, and there's no Xcode project.

## Build and run

```sh
git clone https://github.com/stephenp/lull.git
cd lull/mac
./build.sh            # builds mac/build/Lull.app (universal arm64 + x86_64 when possible)
./build.sh run        # build, then launch
./build.sh install    # build, copy to /Applications, launch
```

`build.sh` compiles the Swift sources with `swiftc`, assembles `Lull.app` with the scene and icon inside, and signs it ad-hoc. If one architecture fails to compile it carries on with the other, and only stops if neither builds.

Once Lull is running, its icon (two slanted bars of light) appears in the menu bar. There's no Dock icon. The wallpaper sits above your desktop picture and below your desktop icons. Allow location access when asked, so the light matches the sun where you are; otherwise Lull estimates your location from your time zone.

For Launch at Login to work, install the app into `/Applications` (`./build.sh install`).

## Sharing with friends

```sh
cd mac
./build.sh dist       # builds mac/build/Lull-1.0.dmg and reveals it in Finder
```

The disk image holds `Lull.app`, an Applications shortcut to drag it onto, and `How to open Lull.txt`. Send the `.dmg` by AirDrop, Messages or a shared folder.

Lull isn't signed by a registered Apple developer, so macOS blocks it the first time someone else opens it:

- **macOS 15 Sequoia and newer:** System Settings → Privacy & Security → **Open Anyway**, once.
- **macOS 13–14:** right-click the app → Open.
- **Terminal alternative:** `xattr -dr com.apple.quarantine /Applications/Lull.app`

The note inside the disk image walks through it.

**Optional: a self-signed certificate.** The default ad-hoc signature works, but every rebuild looks like a new app to macOS, so location access and Launch at Login can reset after an update. To keep them, create a certificate once in Keychain Access → Certificate Assistant → Create a Certificate (Certificate Type: *Code Signing*), then build with it:

```sh
LULL_SIGN_IDENTITY="Lull Self-Signed" ./build.sh dist
```

Only an Apple Developer ID with notarization removes the one-time Open Anyway step.

## Menu bar options

| Item | What it does |
|---|---|
| (header) | The location the sun is computed for. Location is automatic (CoreLocation, refreshed every 3 hours and on wake). If access is denied, Lull estimates from your time zone and shows **Allow Location Access…** |
| Window | Blinds / Window Frame / Arched Window |
| Window Decoration | None / Foliage (Tree Outside) / Sheer Curtain |
| Wall | Knockdown / Limewash / Oak Panels. The wall is seen from across the room, so texture is subtle and shows most in low morning and evening light |
| Dynamic Environment | A gentle breeze through the leaves, curtain and blinds. Off means a still scene that redraws every 5 s, the lightest option on battery |
| Preview Day | Plays the day fast (24 h in about a minute) and opens a floating bar above the Dock with a scrubber, play/pause, and **Done** to return to live time |
| Show Time | Bottom-left: the time in your Mac's 12/24-hour format, the date, time until sunset/sunrise, and today's sun curve. The text turns dark over bright light, and the block drifts slowly to prevent burn-in |
| Low Power Mode | Redraws rarely, at lower resolution |
| Launch at Login | Registers with `SMAppService` (install to /Applications first) |
| Reload Wallpaper | Recreates the wallpaper windows if something looks stuck |

Settings live in `UserDefaults` under `com.stephenp.lull`.

## Working on the scene

The scene is a self-contained WebGL page, so you can work on it in a browser without the Mac app:

```sh
open web/index.html
open "web/index.html?lat=38.58&lon=-121.49&scene=arch&deco=foliage&wall=wood&hud=1&speed=600"
```

- **Parameters:** `lat`, `lon`, `t` (fixed local time, e.g. `2026-06-21T19:30:00`), `scene` (`blinds|window|arch`), `deco` (`none|foliage|curtain`), `wall` (`knockdown|limewash|wood`), `dyn=0` (still scene), `hud=1` (show time), `speed` (time multiplier).
- **Keys:** ←/→ scrub 15 minutes, `1`/`2`/`3` window, `d` decoration, `w` wall, `e` breeze, `h` time display, `n` back to live time. With the time display showing during a preview, you can drag across the sun curve.
- **Edit `web/src.html`, then run `python3 web/build.py`.** That inlines the sun math (`web/solar.js`) into the self-contained `web/index.html` that the app loads.
- **JS API:** the app talks to the page through `window.lull.setConfig({...})` and `window.lull.getState()`, described in [`CONTRACT.md`](CONTRACT.md).
- **Dev tools** (Python + Playwright; set `CHROME=/path/to/chrome` to use a specific browser):
  - `tools/shoot.py out/ name='t=…&scene=…'` renders screenshots.
  - `tools/bench.py` prints the relative GPU cost per redraw. It uses a software renderer, so only ratios between scenes are meaningful.

## How it works

- **Mac app** (`mac/Sources`, AppKit): one borderless, click-through window per display at desktop level on every Space, each hosting the scene in a `WKWebView`. It also handles the menu bar item, the Preview Day bar, location, and pausing when a display is covered or asleep.
- **Rendering**, in three GPU passes:
  1. The wall's color and surface relief, drawn once.
  2. The light through the window and decoration, at half resolution and only where light can land.
  3. A cheap full-resolution pass that combines them with sun color, skylight, room bounce, night grading and tone mapping.
- **Frame rate:** follows how much is moving, from 20 fps for fluttering leaves to one redraw every 5 s for a still scene.
- **Sun:** NOAA solar position sets the light's height, color and timing. The patch's left–right travel follows the day's progress so it stays on screen.
- **Night:** a fixed cool moonlight with a slight blue shift, the way eyes and film see low light.

## Troubleshooting

- **Nothing shows on the desktop:** quit other wallpaper apps, try Reload Wallpaper, and check the logs: `log stream --predicate 'process == "Lull"'`.
- **The wallpaper looks frozen when it shouldn't:** Lull pauses while a display's desktop is fully covered. If macOS ever misreports that, turn it off with `defaults write com.stephenp.lull pauseWhenCovered -bool false`.
- **Only one display gets the wallpaper:** try Reload Wallpaper after connecting a display, and check System Settings → Desktop & Dock → "Displays have separate Spaces".
- **Location doesn't work:** allow Lull in System Settings → Privacy & Security → Location Services (the menu has a shortcut).
- **Launch at Login fails:** run `./build.sh install` so the app lives in `/Applications`, or add it manually under System Settings → General → Login Items.

## Layout

```
mac/Sources/        Swift: AppDelegate, WallpaperWindow, PreviewPanel, MenuBarIcon, Settings, LocationProvider, main
mac/build.sh        build / run / install / dist
mac/Info.plist      bundle info (version, location usage text)
mac/icon.png        1024 px app icon, turned into AppIcon.icns at build time
mac/dist/           the note that ships inside the DMG
web/src.html        the scene source
web/solar.js        NOAA sun position and sunrise/sunset
web/index.html      built, self-contained scene (what the app loads)
tools/              screenshot and benchmark scripts
CONTRACT.md         JS API between the app and the scene
```

## License

[MIT](LICENSE) © 2026 Stephen Pontes. Use it, fork it, change it; just keep the copyright notice.

# Interface contract between web renderer and mac shell

web/index.html is self-contained (inline JS/CSS, no network). It is loaded by WKWebView via loadFileURL.

Global JS API the shell calls (via evaluateJavaScript):
  window.lull.setConfig({
    lat: Number, lon: Number,          // degrees
    scene: "blinds"|"window"|"arch",          // legacy "leaves" maps to window + foliage
    decoration: "none"|"foliage"|"curtain",
    wall: "knockdown"|"limewash"|"wood",
    dynamic: Boolean,                  // gentle breeze; false = still scene, redraw every 5 s
    timeOverride: null | Number,       // epoch ms; null = live clock
    speed: Number,                     // 1 = realtime (preview multiplier)
    showHUD: Boolean,                  // time / date / sun-curve display
    lowPower: Boolean,                 // redraw every 10 s at reduced resolution
    paused: Boolean                    // set per window by the app: desktop covered or displays asleep
  })
  window.lull.getState() -> {sunrise, sunset, altitude, azimuth, now}  (epoch ms / degrees)

URL query params also accepted for testing: ?lat=48.137&lon=11.575&t=2026-06-21T06:30:00&scene=blinds&hud=1&speed=600
Defaults: Munich, live time, blinds, foliage, knockdown, dynamic on, hud=0.
The page posts window.webkit.messageHandlers.lull.postMessage({type:"ready"}) if available.

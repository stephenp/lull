// Renders a frame of the scene with WKWebView, for the screen saver's thumbnail in System Settings.
//   swiftc -O tools/saver_thumbnail.swift -o /tmp/thumb
//   /tmp/thumb web/index.html 'lat=38.58&lon=-121.46&t=2026-06-21T19:55:00&scene=window&deco=foliage&hud=0' 1440 928 big.png
//   sips -c 1276 1980 --cropOffset 180 900 big.png
//   sips -z 116 180 big.png --out mac/Saver/thumbnail@2x.png && sips -z 58 90 big.png --out mac/Saver/thumbnail.png
// `t` is read in this Mac's time zone, so pick a lat/lon in it. The window is off screen, so only the
// first frame is captured, which is why the look comes from URL parameters rather than setConfig.
import AppKit
import WebKit
// usage: thumb <index.html> <query> <width> <height> <out.png>
let a = CommandLine.arguments
let w = Double(a[3])!, h = Double(a[4])!
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
let win = NSWindow(contentRect: NSRect(x: -6000, y: -6000, width: w, height: h), styleMask: [.borderless], backing: .buffered, defer: false)
let wv = WKWebView(frame: NSRect(x: 0, y: 0, width: w, height: h))
win.contentView = wv
win.orderFrontRegardless()
let file = URL(fileURLWithPath: a[1])
var comps = URLComponents(url: file, resolvingAgainstBaseURL: false)!
comps.query = a[2]
wv.loadFileURL(comps.url!, allowingReadAccessTo: file.deletingLastPathComponent())
DispatchQueue.main.asyncAfter(deadline: .now() + 4) {
  let cfg = WKSnapshotConfiguration(); cfg.snapshotWidth = NSNumber(value: w)
  wv.takeSnapshot(with: cfg) { img, _ in
    guard let img = img, let t = img.tiffRepresentation, let rep = NSBitmapImageRep(data: t),
          let png = rep.representation(using: .png, properties: [:]) else { print("failed"); exit(1) }
    try! png.write(to: URL(fileURLWithPath: a[5])); print("wrote", a[5], rep.pixelsWide, rep.pixelsHigh); exit(0)
  }
}
app.run()

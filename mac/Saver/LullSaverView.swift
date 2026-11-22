import ScreenSaver
import WebKit

/// Lull as a macOS screen saver: the same scene as the wallpaper, hosted in a WKWebView inside
/// legacyScreenSaver. The look and location come from settings.json in this bundle's Resources, which
/// Lull.app keeps in sync (see mac/Sources/ScreenSaverInstall.swift); without it, app defaults and a
/// time-zone estimate are used.
@objc(LullSaverView)
final class LullSaverView: ScreenSaverView, WKNavigationDelegate {
    private var webView: WKWebView?
    private var isLoaded = false
    private var observing = false

    private static let background = NSColor(calibratedRed: 0.03, green: 0.03, blue: 0.05, alpha: 1)

    override init?(frame: NSRect, isPreview: Bool) {
        super.init(frame: frame, isPreview: isPreview)
        setUp()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setUp()
    }

    private func setUp() {
        // WKWebView animates itself; ScreenSaverView only needs to stay out of the way.
        animationTimeInterval = 60
        wantsLayer = true
        layer?.backgroundColor = Self.background.cgColor
    }

    deinit {
        DistributedNotificationCenter.default().removeObserver(self)
    }

    override func animateOneFrame() {}

    override func startAnimation() {
        super.startAnimation()
        if !observing {
            observing = true
            // Since Sonoma, legacyScreenSaver often never calls stopAnimation and keeps the views alive after the
            // screen saver ends. This notification is the reliable signal, so drop the web view when it arrives.
            DistributedNotificationCenter.default().addObserver(
                self, selector: #selector(screenSaverWillStop(_:)),
                name: NSNotification.Name("com.apple.screensaver.willstop"), object: nil)
        }
        if webView == nil { makeWebView() } else { push(paused: false) }
    }

    override func stopAnimation() {
        super.stopAnimation()
        tearDown()
    }

    @objc private func screenSaverWillStop(_ note: Notification) {
        // The System Settings thumbnail lives on after the real screen saver stops.
        if !isPreview { tearDown() }
    }

    // MARK: Web view

    private var webRoot: URL? {
        Bundle(for: LullSaverView.self).resourceURL?.appendingPathComponent("web", isDirectory: true)
    }

    private func makeWebView() {
        guard let root = webRoot else { return }
        let config = WKWebViewConfiguration()
        config.suppressesIncrementalRendering = true
        config.websiteDataStore = .nonPersistent()     // nothing to keep, and the sandbox is happier
        let controller = WKUserContentController()
        controller.add(ReadyHandler(self), name: "lull")
        config.userContentController = controller

        let wv = WKWebView(frame: bounds, configuration: config)
        wv.autoresizingMask = [.width, .height]
        wv.navigationDelegate = self
        wv.setValue(false, forKey: "drawsBackground")  // show our dark background until the scene draws
        addSubview(wv)
        webView = wv
        isLoaded = false
        wv.loadFileURL(root.appendingPathComponent("index.html"), allowingReadAccessTo: root)
    }

    private func tearDown() {
        guard let wv = webView else { return }
        webView = nil
        isLoaded = false
        wv.navigationDelegate = nil
        wv.configuration.userContentController.removeScriptMessageHandler(forName: "lull")
        wv.stopLoading()
        wv.removeFromSuperview()
    }

    fileprivate func sceneReady() {
        isLoaded = true
        push(paused: false)
    }

    private func push(paused: Bool) {
        guard isLoaded, let wv = webView else { return }
        var cfg = loadConfig()
        cfg["paused"] = paused
        guard let data = try? JSONSerialization.data(withJSONObject: cfg),
              let json = String(data: data, encoding: .utf8) else { return }
        wv.evaluateJavaScript("window.lull && window.lull.setConfig(\(json));") { _, error in
            if let error = error { NSLog("%@", "Lull saver: setConfig failed: \(error.localizedDescription)") }
        }
    }

    /// The wallpaper's look and location as Lull.app last wrote them, over the app's defaults.
    private func loadConfig() -> [String: Any] {
        let loc = SaverShared.estimateLocationFromTimeZone()
        var cfg: [String: Any] = [
            "lat": loc.lat, "lon": loc.lon,
            "scene": "blinds", "decoration": "foliage", "wall": "knockdown",
            "dynamic": true, "showHUD": false,
        ]
        // Read the file fresh each time: legacyScreenSaver outlives settings changes made in the app.
        if let url = Bundle(for: LullSaverView.self).resourceURL?.appendingPathComponent(SaverShared.settingsFileName),
           let data = try? Data(contentsOf: url),
           let saved = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] {
            for key in ["lat", "lon", "scene", "decoration", "wall", "dynamic", "showHUD"] {
                if let value = saved[key], !(value is NSNull) { cfg[key] = value }
            }
        }
        cfg["speed"] = 1
        cfg["timeOverride"] = NSNull()
        // The System Settings thumbnail is tiny: draw it cheaply and without the time.
        cfg["lowPower"] = isPreview
        if isPreview { cfg["showHUD"] = false }
        return cfg
    }

    // MARK: WKNavigationDelegate

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        NSLog("%@", "Lull saver: navigation failed: \(error.localizedDescription)")
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        NSLog("%@", "Lull saver: provisional navigation failed: \(error.localizedDescription)")
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        isLoaded = false
        webView.reload()
    }
}

/// WKUserContentController retains its handlers, so route "ready" through a weak reference.
private final class ReadyHandler: NSObject, WKScriptMessageHandler {
    weak var view: LullSaverView?
    init(_ view: LullSaverView) { self.view = view }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        if let body = message.body as? [String: Any], body["type"] as? String == "ready" {
            view?.sceneReady()
        }
    }
}

import AppKit
import WebKit

/// A borderless window that sits at desktop level on one screen and hosts the scene.
final class WallpaperWindow: NSWindow, WKNavigationDelegate, WKScriptMessageHandler {
    let webView: WKWebView
    private(set) var isLoaded = false
    var onLoaded: ((WallpaperWindow) -> Void)?

    /// Set by the app delegate while the displays sleep.
    var screensAsleep = false { didSet { updatePaused() } }
    private var sentPaused: Bool? = nil
    /// Escape hatch if occlusion reporting ever misbehaves:
    /// `defaults write com.stephenp.lull pauseWhenCovered -bool false`
    private var pauseWhenCovered: Bool {
        UserDefaults.standard.object(forKey: "pauseWhenCovered") as? Bool ?? true
    }

    init(screen: NSScreen, webRoot: URL) {
        let config = WKWebViewConfiguration()
        config.suppressesIncrementalRendering = true
        let controller = WKUserContentController()
        config.userContentController = controller

        webView = WKWebView(frame: NSRect(origin: .zero, size: screen.frame.size), configuration: config)

        super.init(contentRect: screen.frame,
                   styleMask: [.borderless],
                   backing: .buffered,
                   defer: false)

        // WKUserContentController retains its handlers strongly; we remove it in teardown().
        controller.add(self, name: "lull")

        setFrame(screen.frame, display: false)
        level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopWindow)))
        collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        ignoresMouseEvents = true
        isOpaque = true
        hasShadow = false
        backgroundColor = NSColor(calibratedRed: 0.12, green: 0.11, blue: 0.10, alpha: 1.0)
        isReleasedWhenClosed = false
        animationBehavior = .none

        webView.autoresizingMask = [.width, .height]
        webView.navigationDelegate = self
        contentView = webView

        let index = webRoot.appendingPathComponent("index.html")
        webView.loadFileURL(index, allowingReadAccessTo: webRoot)

        // When other windows completely cover the desktop, stop rendering until it's visible again.
        NotificationCenter.default.addObserver(self, selector: #selector(occlusionChanged(_:)),
                                               name: NSWindow.didChangeOcclusionStateNotification, object: self)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    func teardown() {
        NotificationCenter.default.removeObserver(self)
        webView.navigationDelegate = nil
        webView.configuration.userContentController.removeScriptMessageHandler(forName: "lull")
        webView.stopLoading()
        orderOut(nil)
        close()
    }

    @objc private func occlusionChanged(_ note: Notification) {
        updatePaused()
    }

    /// Tell the scene to stop/resume drawing. Only sends when the state actually changes.
    private func updatePaused() {
        guard isLoaded else { return }
        let covered = pauseWhenCovered && !occlusionState.contains(.visible)
        let paused = screensAsleep || covered
        guard paused != sentPaused else { return }
        sentPaused = paused
        webView.evaluateJavaScript("window.lull && window.lull.setConfig({paused: \(paused)});", completionHandler: nil)
    }

    func push(configJSON: String) {
        guard isLoaded else { return }
        let js = "window.lull && window.lull.setConfig(\(configJSON));"
        webView.evaluateJavaScript(js) { _, error in
            if let error = error {
                NSLog("%@", "Lull: setConfig failed: \(error.localizedDescription)")
            }
        }
    }

    // MARK: WKNavigationDelegate

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        isLoaded = true
        sentPaused = nil
        onLoaded?(self)
        updatePaused()
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        NSLog("%@", "Lull: navigation failed: \(error.localizedDescription)")
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        NSLog("%@", "Lull: provisional navigation failed: \(error.localizedDescription)")
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        // Web content crashed (e.g. GPU reset). Reload.
        isLoaded = false
        webView.reload()
    }

    // MARK: WKScriptMessageHandler

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        if let body = message.body as? [String: Any], let type = body["type"] as? String, type == "ready" {
            // didFinish may fire before scripts finish initialising; "ready" guarantees the API exists.
            isLoaded = true
            sentPaused = nil
            onLoaded?(self)
            updatePaused()
        }
    }
}

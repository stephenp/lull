import AppKit
import ServiceManagement
import CoreLocation

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var windows: [WallpaperWindow] = []
    private var statusItem: NSStatusItem!
    private let settings = Settings.shared
    private var runtime = RuntimeState()
    private let locationProvider = LocationProvider()
    private var rebuildWork: DispatchWorkItem?

    private var locationTimer: Timer?
    private var previewPanel: PreviewPanel?
    private var previewTimer: Timer?
    private var scrubPushPending = false

    private let decorations: [(String, String)] = [
        ("None", "none"), ("Foliage (Tree Outside)", "foliage"),
        ("Sheer Curtain", "curtain"),
    ]

    private let walls: [(String, String)] = [
        ("Knockdown", "knockdown"), ("Limewash", "limewash"), ("Oak Panels", "wood"),
    ]

    /// 24 hours in ~60 seconds.
    private let previewSpeed: Double = 1440

    // MARK: Lifecycle

    func applicationDidFinishLaunching(_ notification: Notification) {
        setupStatusItem()
        rebuildWindows()
        ScreenSaverInstall.updateIfStale(settings: saverSettings())

        NotificationCenter.default.addObserver(self,
                                               selector: #selector(screensChanged(_:)),
                                               name: NSApplication.didChangeScreenParametersNotification,
                                               object: nil)

        let ws = NSWorkspace.shared.notificationCenter
        ws.addObserver(self, selector: #selector(screensDidSleep(_:)),
                       name: NSWorkspace.screensDidSleepNotification, object: nil)
        ws.addObserver(self, selector: #selector(screensDidWake(_:)),
                       name: NSWorkspace.screensDidWakeNotification, object: nil)
        ws.addObserver(self, selector: #selector(screensDidWake(_:)),
                       name: NSWorkspace.didWakeNotification, object: nil)
        ws.addObserver(self, selector: #selector(spaceChanged(_:)),
                       name: NSWorkspace.activeSpaceDidChangeNotification, object: nil)

        // Always follow the Mac's real location; refresh every few hours (travel, DST, etc.).
        refreshLocation()
        locationTimer = Timer.scheduledTimer(withTimeInterval: 3 * 60 * 60, repeats: true) { [weak self] _ in
            self?.refreshLocation()
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        windows.forEach { $0.teardown() }
        windows.removeAll()
    }

    // MARK: Windows

    private var webRoot: URL {
        if let url = Bundle.main.resourceURL?.appendingPathComponent("web", isDirectory: true),
           FileManager.default.fileExists(atPath: url.appendingPathComponent("index.html").path) {
            return url
        }
        // Fallback when running the bare binary from the repo (e.g. during development).
        let exe = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent()
        return exe.appendingPathComponent("../../web", isDirectory: true).standardizedFileURL
    }

    private func rebuildWindows() {
        windows.forEach { $0.teardown() }
        windows.removeAll()

        let root = webRoot
        for screen in NSScreen.screens {
            let win = WallpaperWindow(screen: screen, webRoot: root)
            win.onLoaded = { [weak self] w in
                guard let self = self else { return }
                w.push(configJSON: self.configJSON())
            }
            win.screensAsleep = runtime.screensAsleep
            win.orderBack(nil)
            win.orderFront(nil)
            windows.append(win)
        }
    }

    @objc private func screensChanged(_ note: Notification) {
        // Coalesce bursts of screen-change notifications.
        rebuildWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.rebuildWindows() }
        rebuildWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
    }

    @objc private func screensDidSleep(_ note: Notification) {
        runtime.screensAsleep = true
        windows.forEach { $0.screensAsleep = true }      // scenes stop drawing entirely
    }

    @objc private func screensDidWake(_ note: Notification) {
        runtime.screensAsleep = false
        windows.forEach { $0.screensAsleep = false }
        pushConfig()
        if note.name == NSWorkspace.didWakeNotification {
            refreshLocation()
        }
    }

    @objc private func spaceChanged(_ note: Notification) {
        // Keep windows at the back on every space.
        windows.forEach { $0.orderFront(nil) }
    }

    // MARK: Config

    private func configJSON() -> String {
        let loc = effectiveLocation()
        var dict: [String: Any] = [
            "lat": loc.lat,
            "lon": loc.lon,
            "scene": settings.scene,
            "decoration": settings.decoration,
            "wall": settings.wall,
            "dynamic": settings.dynamic,
            "speed": runtime.previewDay ? (runtime.previewPlaying ? previewSpeed : 0.0) : 1.0,
            "showHUD": settings.showHUD || runtime.previewDay,
            "lowPower": settings.lowPower,
        ]
        if runtime.previewDay {
            let midnight = Calendar.current.startOfDay(for: Date())
            let minutes = previewMinutesNow().truncatingRemainder(dividingBy: 1440)
            dict["timeOverride"] = (midnight.timeIntervalSince1970 + minutes * 60.0) * 1000.0
        } else {
            dict["timeOverride"] = NSNull()
        }
        guard let data = try? JSONSerialization.data(withJSONObject: dict, options: []),
              let json = String(data: data, encoding: .utf8) else {
            return "{}"
        }
        return json
    }

    /// CoreLocation fix if we have one, otherwise a rough estimate from the system time zone
    /// (longitude from the UTC offset, latitude from the hemisphere of common zones).
    private func effectiveLocation() -> (lat: Double, lon: Double) {
        settings.lastLocation ?? SaverShared.estimateLocationFromTimeZone()
    }

    private func refreshLocation() {
        locationProvider.requestLocation { [weak self] result in
            guard let self = self else { return }
            switch result {
            case .success(let coord):
                self.settings.lastLocation = (lat: coord.latitude, lon: coord.longitude)
                self.runtime.locationProblem = nil
                self.pushConfig()
            case .failure(let err):
                NSLog("%@", "Lull: \(err.message)")
                self.runtime.locationProblem = err.message
                self.pushConfig()
            }
        }
    }

    private func pushConfig() {
        if runtime.previewDay {
            // Re-anchor the preview clock so the scene and the scrubber stay in lockstep.
            runtime.previewBase = previewMinutesNow().truncatingRemainder(dividingBy: 1440)
            runtime.previewSetAt = Date()
        }
        let json = configJSON()
        windows.forEach { $0.push(configJSON: json) }
        ScreenSaverInstall.sync(settings: saverSettings())
    }

    /// The look and location the screen saver mirrors (never the Preview Day clock).
    private func saverSettings() -> [String: Any] {
        let loc = effectiveLocation()
        return [
            "lat": loc.lat, "lon": loc.lon,
            "scene": settings.scene, "decoration": settings.decoration, "wall": settings.wall,
            "dynamic": settings.dynamic, "showHUD": settings.showHUD,
        ]
    }

    // MARK: Status item & menu

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            button.image = MenuBarIcon.make()
            button.toolTip = "Lull"
        }
        let menu = NSMenu()
        menu.delegate = self
        menu.autoenablesItems = false
        statusItem.menu = menu
    }

    /// Rebuild the menu each time it opens so check marks are always current.
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        let loc = effectiveLocation()
        let locText: String
        if runtime.locationProblem != nil && settings.lastLocation == nil {
            locText = "Approximate location (from time zone)"
        } else {
            locText = String(format: "Following the sun at %.2f, %.2f", loc.lat, loc.lon)
        }
        let header = NSMenuItem(title: locText, action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        if runtime.locationProblem != nil {
            menu.addItem(makeItem("Allow Location Access…", #selector(openLocationSettings(_:))))
        }
        menu.addItem(.separator())

        // Scene (window type)
        let sceneItem = NSMenuItem(title: "Window", action: nil, keyEquivalent: "")
        let sceneMenu = NSMenu()
        for (title, value) in [("Blinds", "blinds"), ("Window Frame", "window"), ("Arched Window", "arch")] {
            let item = makeItem(title, #selector(selectScene(_:)))
            item.representedObject = value
            item.state = settings.scene == value ? .on : .off
            sceneMenu.addItem(item)
        }
        sceneItem.submenu = sceneMenu
        menu.addItem(sceneItem)

        // Window decoration
        let decoItem = NSMenuItem(title: "Window Decoration", action: nil, keyEquivalent: "")
        let decoMenu = NSMenu()
        for (title, value) in decorations {
            let item = makeItem(title, #selector(selectDecoration(_:)))
            item.representedObject = value
            item.state = settings.decoration == value ? .on : .off
            decoMenu.addItem(item)
        }
        decoItem.submenu = decoMenu
        menu.addItem(decoItem)

        // Wall surface
        let wallItem = NSMenuItem(title: "Wall", action: nil, keyEquivalent: "")
        let wallMenu = NSMenu()
        for (title, value) in walls {
            let item = makeItem(title, #selector(selectWall(_:)))
            item.representedObject = value
            item.state = settings.wall == value ? .on : .off
            wallMenu.addItem(item)
        }
        wallItem.submenu = wallMenu
        menu.addItem(wallItem)

        let dyn = makeItem("Dynamic Environment", #selector(toggleDynamic(_:)))
        dyn.state = settings.dynamic ? .on : .off
        dyn.toolTip = "A gentle breeze through the plants, trees, curtain and blinds."
        menu.addItem(dyn)

        menu.addItem(.separator())

        let preview = makeItem("Preview Day", #selector(togglePreview(_:)))
        preview.state = runtime.previewDay ? .on : .off
        menu.addItem(preview)

        let hud = makeItem("Show Time", #selector(toggleHUD(_:)))
        hud.state = settings.showHUD ? .on : .off
        menu.addItem(hud)

        let low = makeItem("Low Power Mode", #selector(toggleLowPower(_:)))
        low.state = settings.lowPower ? .on : .off
        menu.addItem(low)

        let login = makeItem("Launch at Login", #selector(toggleLaunchAtLogin(_:)))
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(login)

        menu.addItem(.separator())
        if ScreenSaverInstall.isInstalled {
            menu.addItem(makeItem("Screen Saver Settings…", #selector(openScreenSaverSettings(_:))))
        } else {
            let saver = makeItem("Install Screen Saver…", #selector(installScreenSaver(_:)))
            saver.toolTip = "Adds Lull to System Settings → Screen Saver, with the same look as your wallpaper."
            menu.addItem(saver)
        }
        menu.addItem(makeItem("Reload Wallpaper", #selector(reloadWallpaper(_:))))
        let quit = NSMenuItem(title: "Quit Lull", action: #selector(quit(_:)), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
    }

    private func makeItem(_ title: String, _ action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        item.isEnabled = true
        return item
    }

    // MARK: Actions

    @objc private func selectScene(_ sender: NSMenuItem) {
        guard let value = sender.representedObject as? String else { return }
        settings.scene = value
        pushConfig()
    }

    @objc private func selectDecoration(_ sender: NSMenuItem) {
        guard let value = sender.representedObject as? String else { return }
        settings.decoration = value
        pushConfig()
    }

    @objc private func selectWall(_ sender: NSMenuItem) {
        guard let value = sender.representedObject as? String else { return }
        settings.wall = value
        pushConfig()
    }

    @objc private func toggleDynamic(_ sender: NSMenuItem) {
        settings.dynamic.toggle()
        pushConfig()
    }

    @objc private func openLocationSettings(_ sender: NSMenuItem) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_LocationServices") {
            NSWorkspace.shared.open(url)
        }
        // Try again in case the user just granted access.
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in self?.refreshLocation() }
    }

    @objc private func togglePreview(_ sender: NSMenuItem) {
        if runtime.previewDay { stopPreview() } else { startPreview() }
    }

    // MARK: Preview Day

    /// Unwrapped preview time in minutes since local midnight (may exceed 1440 until the timer wraps it).
    private func previewMinutesNow() -> Double {
        let elapsed = runtime.previewPlaying ? Date().timeIntervalSince(runtime.previewSetAt) * previewSpeed / 60.0 : 0
        return runtime.previewBase + elapsed
    }

    private func startPreview() {
        let comps = Calendar.current.dateComponents([.hour, .minute, .second], from: Date())
        runtime.previewBase = Double(comps.hour ?? 0) * 60 + Double(comps.minute ?? 0) + Double(comps.second ?? 0) / 60
        runtime.previewSetAt = Date()
        runtime.previewPlaying = true
        runtime.previewDay = true

        let panel = previewPanel ?? PreviewPanel()
        previewPanel = panel
        panel.onScrub = { [weak self] minutes in
            guard let self = self else { return }
            self.runtime.previewBase = minutes
            self.runtime.previewSetAt = Date()
            self.schedulePreviewPush()
        }
        panel.onTogglePlay = { [weak self] in
            guard let self = self else { return }
            self.runtime.previewBase = self.previewMinutesNow().truncatingRemainder(dividingBy: 1440)
            self.runtime.previewSetAt = Date()
            self.runtime.previewPlaying.toggle()
            self.previewPanel?.setPlaying(self.runtime.previewPlaying)
            self.pushConfig()
        }
        panel.onDone = { [weak self] in self?.stopPreview() }
        panel.setPlaying(true)
        panel.setMinutes(runtime.previewBase)
        panel.positionOnMainScreen()
        panel.orderFrontRegardless()

        previewTimer?.invalidate()
        previewTimer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            self?.previewTick()
        }
        pushConfig()
    }

    /// Slider events can arrive at 100+ Hz; coalesce them so each display's web view gets at most ~30 updates/s.
    private func schedulePreviewPush() {
        guard !scrubPushPending else { return }
        scrubPushPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0 / 30.0) { [weak self] in
            guard let self = self else { return }
            self.scrubPushPending = false
            self.pushConfig()
        }
    }

    private func previewTick() {
        guard runtime.previewDay else { return }
        let raw = previewMinutesNow()
        if raw >= 1440 {
            // Loop the day: jump the scene back to just after midnight.
            runtime.previewBase = raw.truncatingRemainder(dividingBy: 1440)
            runtime.previewSetAt = Date()
            pushConfig()
        }
        previewPanel?.setMinutes(raw.truncatingRemainder(dividingBy: 1440))
    }

    private func stopPreview() {
        runtime.previewDay = false
        previewTimer?.invalidate()
        previewTimer = nil
        previewPanel?.orderOut(nil)
        pushConfig()
    }

    @objc private func toggleHUD(_ sender: NSMenuItem) {
        settings.showHUD.toggle()
        pushConfig()
    }

    @objc private func toggleLowPower(_ sender: NSMenuItem) {
        settings.lowPower.toggle()
        pushConfig()
    }

    @objc private func toggleLaunchAtLogin(_ sender: NSMenuItem) {
        let service = SMAppService.mainApp
        do {
            if service.status == .enabled {
                try service.unregister()
            } else {
                try service.register()
            }
        } catch {
            showAlert(title: "Launch at Login",
                      message: "Couldn't change the login item: \(error.localizedDescription)\n\nTip: move Lull.app to /Applications first, or add it manually in System Settings → General → Login Items.")
        }
    }

    @objc private func installScreenSaver(_ sender: NSMenuItem) {
        if let error = ScreenSaverInstall.install(settings: saverSettings()) {
            showAlert(title: "Install Screen Saver", message: "Couldn't install the screen saver: \(error.localizedDescription)")
            return
        }
        ScreenSaverInstall.openSystemSettings()
    }

    @objc private func openScreenSaverSettings(_ sender: NSMenuItem) {
        ScreenSaverInstall.openSystemSettings()
    }

    @objc private func reloadWallpaper(_ sender: NSMenuItem) {
        rebuildWindows()
    }

    @objc private func quit(_ sender: NSMenuItem) {
        NSApp.terminate(nil)
    }

    private func showAlert(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: "OK")
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }
}

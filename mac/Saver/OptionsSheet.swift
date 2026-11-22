import AppKit
import ScreenSaver

/// The screen saver's own look, set from its Options… sheet in System Settings. By default it matches
/// the wallpaper (the look Lull.app mirrors into settings.json); turning that off uses these values instead.
/// Stored with ScreenSaverDefaults, which lives in legacyScreenSaver's sandbox container.
struct SaverOptions {
    static let lookKeys = ["scene", "decoration", "wall", "dynamic", "showHUD"]
    private static let moduleName = "com.stephenp.lull.saver"

    var matchWallpaper = true
    var look: [String: Any] = [:]

    static func load() -> SaverOptions {
        var o = SaverOptions()
        guard let d = ScreenSaverDefaults(forModuleWithName: moduleName) else { return o }
        o.matchWallpaper = d.object(forKey: "matchWallpaper") as? Bool ?? true
        for key in lookKeys { if let v = d.object(forKey: key) { o.look[key] = v } }
        return o
    }

    func save() {
        guard let d = ScreenSaverDefaults(forModuleWithName: Self.moduleName) else { return }
        d.set(matchWallpaper, forKey: "matchWallpaper")
        for (key, value) in look { d.set(value, forKey: key) }
        d.synchronize()
    }
}

/// The Options… sheet: "Match my wallpaper", or the same choices as Lull's menu.
final class OptionsSheet: NSObject {
    let window: NSWindow
    private let onSave: () -> Void

    private let match = NSButton(checkboxWithTitle: "Match my wallpaper", target: nil, action: nil)
    private let scene = NSPopUpButton()
    private let decoration = NSPopUpButton()
    private let wall = NSPopUpButton()
    private let dynamic = NSButton(checkboxWithTitle: "Dynamic Environment", target: nil, action: nil)
    private let showHUD = NSButton(checkboxWithTitle: "Show Time", target: nil, action: nil)

    /// `wallpaper` is the wallpaper's current look, used while matching it and as the starting point.
    init(wallpaper: [String: Any], onSave: @escaping () -> Void) {
        self.onSave = onSave
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 400, height: 300),
                          styleMask: [.titled], backing: .buffered, defer: true)
        super.init()

        let options = SaverOptions.load()
        var look = wallpaper
        if !options.matchWallpaper { look.merge(options.look) { _, own in own } }

        match.target = self
        match.action = #selector(matchChanged(_:))
        match.state = options.matchWallpaper ? .on : .off
        let note = NSTextField(wrappingLabelWithString:
            "Uses the window, wall and time display you chose from Lull's menu bar icon.")
        note.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        note.textColor = .secondaryLabelColor

        Self.fill(scene, SaverShared.scenes, look["scene"])
        Self.fill(decoration, SaverShared.decorations, look["decoration"])
        Self.fill(wall, SaverShared.walls, look["wall"])
        for popup in [scene, decoration, wall] { popup.widthAnchor.constraint(equalToConstant: 210).isActive = true }
        dynamic.state = (look["dynamic"] as? Bool ?? true) ? .on : .off
        dynamic.toolTip = "A gentle breeze through the plants, trees, curtain and blinds."
        showHUD.state = (look["showHUD"] as? Bool ?? false) ? .on : .off

        let grid = NSGridView(views: [
            [Self.label("Window:"), scene],
            [Self.label("Window Decoration:"), decoration],
            [Self.label("Wall:"), wall],
            [NSGridCell.emptyContentView, dynamic],
            [NSGridCell.emptyContentView, showHUD],
        ])
        grid.column(at: 0).xPlacement = .trailing
        grid.rowAlignment = .firstBaseline
        grid.rowSpacing = 10
        grid.columnSpacing = 8

        let cancel = NSButton(title: "Cancel", target: self, action: #selector(cancel(_:)))
        cancel.keyEquivalent = "\u{1b}"
        let ok = NSButton(title: "OK", target: self, action: #selector(ok(_:)))
        ok.keyEquivalent = "\r"
        let buttons = NSStackView(views: [NSView(), cancel, ok])
        buttons.orientation = .horizontal

        let stack = NSStackView(views: [match, note, grid, buttons])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 12
        stack.setCustomSpacing(4, after: match)
        stack.setCustomSpacing(18, after: note)
        stack.setCustomSpacing(20, after: grid)
        stack.edgeInsets = NSEdgeInsets(top: 20, left: 20, bottom: 20, right: 20)
        stack.translatesAutoresizingMaskIntoConstraints = false
        buttons.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -40).isActive = true
        note.widthAnchor.constraint(equalTo: buttons.widthAnchor).isActive = true

        let content = NSView()
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.widthAnchor.constraint(equalToConstant: 420),
        ])
        window.contentView = content
        window.setContentSize(content.fittingSize)
        updateEnabled()
    }

    private static func label(_ text: String) -> NSTextField { NSTextField(labelWithString: text) }

    private static func fill(_ popup: NSPopUpButton, _ items: [(String, String)], _ selected: Any?) {
        popup.addItems(withTitles: items.map { $0.0 })
        popup.selectItem(at: items.firstIndex { $0.1 == selected as? String } ?? 0)
    }

    private static func value(_ popup: NSPopUpButton, _ items: [(String, String)]) -> String {
        items[max(0, min(items.count - 1, popup.indexOfSelectedItem))].1
    }

    private func updateEnabled() {
        let own = match.state == .off
        [scene, decoration, wall, dynamic, showHUD].forEach { $0.isEnabled = own }
    }

    @objc private func matchChanged(_ sender: NSButton) { updateEnabled() }

    @objc private func ok(_ sender: NSButton) {
        var options = SaverOptions()
        options.matchWallpaper = match.state == .on
        options.look = [
            "scene": Self.value(scene, SaverShared.scenes),
            "decoration": Self.value(decoration, SaverShared.decorations),
            "wall": Self.value(wall, SaverShared.walls),
            "dynamic": dynamic.state == .on,
            "showHUD": showHUD.state == .on,
        ]
        options.save()
        close()
        onSave()
    }

    @objc private func cancel(_ sender: NSButton) { close() }

    private func close() {
        window.sheetParent?.endSheet(window)
        window.orderOut(nil)
    }
}

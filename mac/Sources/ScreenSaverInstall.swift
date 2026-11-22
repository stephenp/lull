import AppKit

/// Installs the Lull.saver bundle that ships inside Lull.app into ~/Library/Screen Savers, keeps it up to
/// date, and mirrors the wallpaper's look and location into it (legacyScreenSaver is sandboxed, so the
/// saver can't read Lull's preferences, but it can always read its own bundle).
enum ScreenSaverInstall {
    private static let fm = FileManager.default

    static var embeddedURL: URL? {
        guard let url = Bundle.main.resourceURL?.appendingPathComponent(SaverShared.bundleName, isDirectory: true),
              fm.fileExists(atPath: url.path) else { return nil }
        return url
    }

    static var installedURL: URL {
        fm.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Screen Savers", isDirectory: true)
            .appendingPathComponent(SaverShared.bundleName, isDirectory: true)
    }

    static var isInstalled: Bool { fm.fileExists(atPath: installedURL.path) }

    /// Copies the embedded saver into place, replacing an older copy, then writes `settings`.
    @discardableResult
    static func install(settings: [String: Any]) -> Error? {
        guard let source = embeddedURL else {
            return NSError(domain: "Lull", code: 1, userInfo: [NSLocalizedDescriptionKey:
                "This copy of Lull doesn't include the screen saver. Rebuild it with mac/build.sh."])
        }
        do {
            try fm.createDirectory(at: installedURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            if isInstalled { try fm.removeItem(at: installedURL) }
            try fm.copyItem(at: source, to: installedURL)
        } catch {
            NSLog("%@", "Lull: installing the screen saver failed: \(error.localizedDescription)")
            return error
        }
        // The copy inherits the download's quarantine flag, which stops legacyScreenSaver loading it.
        run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", installedURL.path])
        lastWritten = nil
        sync(settings: settings)
        // legacyScreenSaver keeps loaded savers in memory; restart it so the new copy is used.
        run("/usr/bin/killall", ["legacyScreenSaver"])
        return nil
    }

    /// On launch: if an installed saver came from a different build, replace it with this one.
    static func updateIfStale(settings: [String: Any]) {
        guard isInstalled, let source = embeddedURL else { return }
        if version(of: source) != version(of: installedURL) {
            NSLog("%@", "Lull: updating the installed screen saver")
            install(settings: settings)
        } else {
            sync(settings: settings)
        }
    }

    private static var lastWritten: Data?

    /// Writes the wallpaper's look and location into the installed saver (only when it changed).
    static func sync(settings: [String: Any]) {
        guard isInstalled,
              let data = try? JSONSerialization.data(withJSONObject: settings, options: [.sortedKeys]),
              data != lastWritten else { return }
        let url = installedURL.appendingPathComponent("Contents/Resources", isDirectory: true)
            .appendingPathComponent(SaverShared.settingsFileName)
        do {
            try data.write(to: url, options: .atomic)
            lastWritten = data
        } catch {
            NSLog("%@", "Lull: couldn't update the screen saver's settings: \(error.localizedDescription)")
        }
    }

    static func openSystemSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.ScreenSaver-Settings.extension") {
            NSWorkspace.shared.open(url)
        }
    }

    private static func version(of bundle: URL) -> String? {
        let info = NSDictionary(contentsOf: bundle.appendingPathComponent("Contents/Info.plist"))
        let short = info?["CFBundleShortVersionString"] as? String ?? ""
        let build = info?["CFBundleVersion"] as? String ?? ""
        // The executable's size and date catch rebuilds that didn't bump the version (development).
        let exe = bundle.appendingPathComponent("Contents/MacOS/LullSaver")
        let attrs = try? fm.attributesOfItem(atPath: exe.path)
        let size = (attrs?[.size] as? NSNumber)?.intValue ?? 0
        let date = (attrs?[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
        return info == nil ? nil : "\(short)-\(build)-\(size)-\(Int(date))"
    }

    private static func run(_ path: String, _ args: [String]) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run(); p.waitUntilExit() } catch {
            NSLog("%@", "Lull: \(path) failed: \(error.localizedDescription)")
        }
    }
}

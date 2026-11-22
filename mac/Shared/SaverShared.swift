import Foundation

/// Code compiled into both Lull.app and Lull.saver.
enum SaverShared {
    /// Bundle name of the screen saver, inside Lull.app/Contents/Resources and in ~/Library/Screen Savers.
    static let bundleName = "Lull.saver"
    /// The app mirrors the wallpaper's look and location into this file inside the installed saver, because
    /// legacyScreenSaver is sandboxed and can't read Lull's own preferences. The saver reads it from its bundle.
    static let settingsFileName = "settings.json"

    /// Rough location from the system time zone: longitude from the standard UTC offset,
    /// latitude from the hemisphere of common zones. Used until (or unless) CoreLocation answers.
    static func estimateLocationFromTimeZone() -> (lat: Double, lon: Double) {
        let tz = TimeZone.current
        let offsetHours = Double(tz.secondsFromGMT() - Int(tz.daylightSavingTimeOffset())) / 3600.0
        let southern = ["Australia/", "Pacific/Auckland", "America/Sao_Paulo", "America/Argentina", "America/Santiago",
                        "Africa/Johannesburg"].contains { tz.identifier.hasPrefix($0) }
        return (lat: southern ? -33.0 : 40.0, lon: offsetHours * 15.0)
    }
}

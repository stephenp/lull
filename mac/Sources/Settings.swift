import Foundation

/// Persisted user settings. Everything lives in UserDefaults.standard.
final class Settings {
    static let shared = Settings()

    private let defaults = UserDefaults.standard

    private enum Key {
        static let lat = "lat"
        static let lon = "lon"
        static let hasLocation = "hasLocation"
        static let scene = "scene"
        static let decoration = "decoration"
        static let wall = "wall"
        static let dynamic = "dynamic"
        static let showHUD = "showHUD"
        static let lowPower = "lowPower"
    }

    private init() {
        // The app used to be called "Sunstill": carry its settings over once.
        if !defaults.bool(forKey: "migratedFromSunstill") {
            if let old = UserDefaults(suiteName: "com.stephenp.sunstill-clone") {
                for key in ["lat", "lon", "hasLocation", "scene", "decoration", "wall", "dynamic", "showHUD", "lowPower", "pauseWhenCovered"] {
                    if let value = old.object(forKey: key), defaults.object(forKey: key) == nil {
                        defaults.set(value, forKey: key)
                    }
                }
            }
            defaults.set(true, forKey: "migratedFromSunstill")
        }
        defaults.register(defaults: [
            Key.hasLocation: false,
            Key.scene: "blinds",
            Key.decoration: "foliage",
            Key.wall: "knockdown",
            Key.dynamic: true,
            Key.showHUD: false,
            Key.lowPower: false,
        ])
        // Migrate the old "leaves" scene to window + foliage decoration.
        if defaults.string(forKey: Key.scene) == "leaves" {
            defaults.set("window", forKey: Key.scene)
            defaults.set("foliage", forKey: Key.decoration)
        }
        // Options removed in later builds fall back to sensible defaults.
        if let w = defaults.string(forKey: Key.wall), ["plaster", "brick", "linen", "concrete"].contains(w) {
            defaults.set("knockdown", forKey: Key.wall)
        }
        if let d = defaults.string(forKey: Key.decoration), ["glass", "prism"].contains(d) {
            defaults.set("none", forKey: Key.decoration)
        }
        if defaults.string(forKey: Key.decoration) == "monstera" {
            defaults.set("foliage", forKey: Key.decoration)
        }
        defaults.removeObject(forKey: "weather")   // weather options removed: always clear
        // Old builds stored a manually chosen city; location is now always automatic.
        defaults.removeObject(forKey: "locationName")
        defaults.removeObject(forKey: "useCurrentLocation")
    }

    /// Last location reported by CoreLocation (cached so launches work offline).
    var lastLocation: (lat: Double, lon: Double)? {
        get {
            guard defaults.bool(forKey: Key.hasLocation) else { return nil }
            return (defaults.double(forKey: Key.lat), defaults.double(forKey: Key.lon))
        }
        set {
            if let loc = newValue {
                defaults.set(loc.lat, forKey: Key.lat)
                defaults.set(loc.lon, forKey: Key.lon)
                defaults.set(true, forKey: Key.hasLocation)
            } else {
                defaults.set(false, forKey: Key.hasLocation)
            }
        }
    }

    var scene: String {
        get { defaults.string(forKey: Key.scene) ?? "blinds" }
        set { defaults.set(newValue, forKey: Key.scene) }
    }

    var decoration: String {
        get { defaults.string(forKey: Key.decoration) ?? "foliage" }
        set { defaults.set(newValue, forKey: Key.decoration) }
    }

    var wall: String {
        get { defaults.string(forKey: Key.wall) ?? "knockdown" }
        set { defaults.set(newValue, forKey: Key.wall) }
    }

    var dynamic: Bool {
        get { defaults.bool(forKey: Key.dynamic) }
        set { defaults.set(newValue, forKey: Key.dynamic) }
    }

    var showHUD: Bool {
        get { defaults.bool(forKey: Key.showHUD) }
        set { defaults.set(newValue, forKey: Key.showHUD) }
    }

    var lowPower: Bool {
        get { defaults.bool(forKey: Key.lowPower) }
        set { defaults.set(newValue, forKey: Key.lowPower) }
    }
}

/// Runtime-only state that is not persisted.
struct RuntimeState {
    var previewDay: Bool = false
    var previewPlaying: Bool = true
    /// Preview clock: minutes since local midnight at `previewSetAt`.
    var previewBase: Double = 0
    var previewSetAt: Date = Date()
    var screensAsleep: Bool = false
    /// Set when CoreLocation is denied/unavailable and we fall back to a time-zone estimate.
    var locationProblem: String? = nil
}

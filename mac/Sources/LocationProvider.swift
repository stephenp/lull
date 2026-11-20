import Foundation
import CoreLocation

/// One-shot CoreLocation lookup. Calls `completion` once with coordinates or an error message.
final class LocationProvider: NSObject, CLLocationManagerDelegate {
    private let manager = CLLocationManager()
    private var completion: ((Result<CLLocationCoordinate2D, LocationError>) -> Void)?

    struct LocationError: Error {
        let message: String
    }

    override init() {
        super.init()
        manager.delegate = self
        manager.desiredAccuracy = kCLLocationAccuracyKilometer
    }

    func requestLocation(_ completion: @escaping (Result<CLLocationCoordinate2D, LocationError>) -> Void) {
        self.completion = completion

        guard CLLocationManager.locationServicesEnabled() else {
            finish(.failure(LocationError(message: "Location Services are turned off. Enable them in System Settings → Privacy & Security → Location Services.")))
            return
        }

        switch manager.authorizationStatus {
        case .notDetermined:
            // Delegate callback locationManagerDidChangeAuthorization will continue.
            manager.requestWhenInUseAuthorization()
        case .denied, .restricted:
            finish(.failure(LocationError(message: "Lull doesn't have permission to use your location. Allow it in System Settings → Privacy & Security → Location Services, or set a location manually.")))
        default:
            manager.requestLocation()
        }
    }

    private func finish(_ result: Result<CLLocationCoordinate2D, LocationError>) {
        guard let completion = completion else { return }
        self.completion = nil
        DispatchQueue.main.async {
            completion(result)
        }
    }

    // MARK: CLLocationManagerDelegate

    func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        guard completion != nil else { return }
        switch manager.authorizationStatus {
        case .notDetermined:
            break
        case .denied, .restricted:
            finish(.failure(LocationError(message: "Location permission was denied. You can set a location manually instead.")))
        default:
            manager.requestLocation()
        }
    }

    func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        if let loc = locations.last {
            finish(.success(loc.coordinate))
        }
    }

    func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        finish(.failure(LocationError(message: "Couldn't determine your location: \(error.localizedDescription)")))
    }
}

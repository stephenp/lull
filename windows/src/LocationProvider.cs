using System;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace Lull;

/// One-shot location lookup through Windows' location service.
/// Mirrors mac/Sources/LocationProvider.swift: returns coordinates or a user-facing problem message.
public static class LocationProvider
{
    public sealed record Result(double Lat, double Lon, string? Problem)
    {
        public bool Ok => Problem == null;
    }

    public static async Task<Result> RequestAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                return new Result(0, 0,
                    "Lull can't use your location. Turn on Location services and " +
                    "\"Let desktop apps access your location\" in Settings → Privacy & security → Location.");
            }

            // ~1 km is plenty for sun angles, and lets Windows answer from Wi-Fi/IP without GPS.
            var locator = new Geolocator { DesiredAccuracyInMeters = 1000 };
            var pos = await locator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromHours(1), timeout: TimeSpan.FromSeconds(20));
            var p = pos.Coordinate.Point.Position;
            return new Result(p.Latitude, p.Longitude, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new Result(0, 0,
                "Location access is turned off for desktop apps. Allow it in Settings → Privacy & security → Location.");
        }
        catch (Exception e)
        {
            return new Result(0, 0, $"Couldn't determine your location: {e.Message}");
        }
    }

    /// Opens the Windows location privacy page.
    public static void OpenSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-location")
            { UseShellExecute = true });
        }
        catch (Exception e) { Log.Write($"open location settings: {e.Message}"); }
    }

    /// Rough fallback from the system time zone, same heuristic as the Mac app:
    /// longitude from the standard UTC offset, latitude from the hemisphere of common zones.
    public static (double lat, double lon) EstimateFromTimeZone()
    {
        var tz = TimeZoneInfo.Local;
        double offsetHours = tz.BaseUtcOffset.TotalHours;
        string id = tz.Id; // Windows ids, e.g. "AUS Eastern Standard Time"
        string[] southern =
        {
            "AUS", "Cen. Australia", "E. Australia", "W. Australia", "Tasmania", "New Zealand",
            "E. South America", "Argentina", "Pacific SA", "South Africa",
        };
        bool south = Array.Exists(southern, s => id.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        return (south ? -33.0 : 40.0, offsetHours * 15.0);
    }
}

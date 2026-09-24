using System.Globalization;

namespace YemenDrive.Application.Places;

/// <summary>
/// Uses a roughly eleven-metre grid so a single physical location cannot be
/// saved repeatedly because of normal GPS jitter.
/// </summary>
public static class SavedPlaceLocationKey
{
    public static string Create(double latitude, double longitude) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Math.Round(latitude, 4):F4}|{Math.Round(longitude, 4):F4}");
}

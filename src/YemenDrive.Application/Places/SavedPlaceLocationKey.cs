using System.Globalization;

namespace YemenDrive.Application.Places;

/// <summary>
/// Uses a roughly eleven-metre grid to match unnamed favourites despite GPS jitter.
/// Named shortcuts may share coordinates and are updated by name or standard kind.
/// </summary>
public static class SavedPlaceLocationKey
{
    public static string Create(double latitude, double longitude) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Math.Round(latitude, 4):F4}|{Math.Round(longitude, 4):F4}");
}

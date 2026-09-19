using System.Globalization;
using System.Text.RegularExpressions;

namespace YemenDrive.Application.Rides;

/// <summary>
/// Keeps coordinate values for navigation only.  All human-facing lists and
/// notifications must use these names, never the raw latitude/longitude text.
/// </summary>
internal static partial class RideLocationText
{
    private static readonly Regex CoordinatePair = CoordinatePairRegex();

    public static string DisplayName(string? label, string? address, string fallback)
    {
        if (IsUsable(label)) return label!.Trim();
        if (IsUsable(address)) return address!.Trim();
        return fallback;
    }

    public static string AreaName(string? label, string? address, string fallback)
    {
        // A label is deliberately supplied by Places/the customer UI as the
        // readable area name.  Do not fall back to a coordinate-looking value.
        return DisplayName(label, address, fallback);
    }

    public static string RouteSummary(string? pickupLabel, string? pickupAddress,
        string? destinationLabel, string? destinationAddress) =>
        $"من {DisplayName(pickupLabel, pickupAddress, "نقطة الانطلاق")} إلى " +
        DisplayName(destinationLabel, destinationAddress, "الوجهة");

    private static bool IsUsable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !CoordinatePair.IsMatch(value.Trim());

    [GeneratedRegex(@"^\(?\s*[-+]?\d{1,3}(?:\.\d+)?\s*[,،]\s*[-+]?\d{1,3}(?:\.\d+)?\s*\)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CoordinatePairRegex();
}

namespace YemenDrive.Application.Maps;

/// <summary>
/// Server-only credentials from protected deployment configuration, environment
/// variables or a managed secret store. Preserve them during upgrades; never
/// save them in the application database or return them to a client.
/// </summary>
public sealed class GoogleMapsOptions
{
    public string RoutesApiKey { get; init; } = string.Empty;
    public string PlacesApiKey { get; init; } = string.Empty;
    public string GeocodingApiKey { get; init; } = string.Empty;
}

public sealed record MapRouteQuoteResult(
    int DistanceMeters,
    int DurationSeconds,
    string EncodedPolyline,
    IReadOnlyList<MapRouteStep> Steps);

public sealed record MapRouteStep(
    string Instruction,
    string Maneuver,
    int DistanceMeters,
    int DurationSeconds);

public sealed record MapPlace(
    string Title,
    string FormattedAddress,
    string Street,
    string Area,
    double Latitude,
    double Longitude);

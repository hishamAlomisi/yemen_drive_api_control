namespace YemenDrive.Application.Maps;

/// <summary>
/// Server-only credentials. Bind these fields from environment variables or a
/// managed secret store; never save them in the database or return them to a
/// client.
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
    string EncodedPolyline);

public sealed record MapPlace(
    string Title,
    string FormattedAddress,
    string Street,
    string Area,
    double Latitude,
    double Longitude);

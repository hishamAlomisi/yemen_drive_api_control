namespace YemenDrive.Application.Maps;

public sealed record MapRouteQuoteModel(
    double OriginLatitude,
    double OriginLongitude,
    double DestinationLatitude,
    double DestinationLongitude);

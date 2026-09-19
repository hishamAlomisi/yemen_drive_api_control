using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Maps;

public sealed class MapRouteQuote(
    GoogleMapsGateway gateway,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : ReportsService<MapRouteQuoteModel>(configurationStore)
{
    protected override async Task<object?> ReportAsync(MapRouteQuoteModel model, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        EnsureCoordinates(model.OriginLatitude, model.OriginLongitude);
        EnsureCoordinates(model.DestinationLatitude, model.DestinationLongitude);
        return await gateway.ComputeRouteAsync(
            model.OriginLatitude,
            model.OriginLongitude,
            model.DestinationLatitude,
            model.DestinationLongitude,
            cancellationToken);
    }

    private void EnsureAuthenticated()
    {
        if (currentUser.UserId is null)
            throw new ServiceException("authentication_required", "سجل الدخول لاستخدام خدمات الخرائط.");
    }

    private static void EnsureCoordinates(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ServiceException("invalid_map_coordinates", "إحداثيات الخريطة غير صالحة.");
    }
}

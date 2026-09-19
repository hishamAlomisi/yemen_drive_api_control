using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Maps;

public sealed class MapLocationSearch(
    GoogleMapsGateway gateway,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : ReportsService<MapLocationSearchModel>(configurationStore)
{
    protected override async Task<object?> SearchAsync(MapLocationSearchModel model, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        var query = model.Query?.Trim() ?? string.Empty;
        if (query.Length is < 2 or > 200)
            throw new ServiceException("invalid_place_query", "اكتب اسم مكان من حرفين إلى 200 حرف.");
        return await gateway.SearchAsync(query, cancellationToken);
    }

    protected override async Task<object?> ReportAsync(MapLocationSearchModel model, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        if (model.Latitude is null || model.Longitude is null)
            throw new ServiceException("invalid_map_coordinates", "إحداثيات الخريطة مطلوبة.");
        EnsureCoordinates(model.Latitude.Value, model.Longitude.Value);
        return await gateway.ReverseGeocodeAsync(model.Latitude.Value, model.Longitude.Value, cancellationToken);
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

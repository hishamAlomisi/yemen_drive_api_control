using Microsoft.AspNetCore.SignalR;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api.Tracking;

public static class TrackingUpgradeEndpoints
{
    public static IServiceCollection AddTrackingUpgrade(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = new TrackingUpgradeSettings();
        configuration.GetSection("Tracking").Bind(settings);
        services.AddSingleton(settings);
        services.AddScoped<RouteSampleBatchService>();
        services.AddSingleton<RideMapWatchStore>();
        services.AddSingleton<DriverLocationVisualQueue>();
        services.AddScoped<IDriverLocationVisualTransport, DriverLocationVisualTransport>();
        services.AddHostedService<DriverLocationVisualWorker>();
        return services;
    }

    public static void MapTrackingUpgrade(this WebApplication app)
    {
        app.MapPost("/api/tracking/driver-location-batch", async (RouteSampleBatchRequest request,
            ICurrentUserContext currentUser, RouteSampleBatchService service, CancellationToken ct) =>
        {
            if (currentUser.UserId is not int driverId) return Results.Unauthorized();
            try { return Results.Ok(ApiResult.Ok(await service.SaveAsync(driverId, request, ct))); }
            catch (HubException error) { return Results.BadRequest(ApiResult.Fail("invalid_location_batch", error.Message)); }
            catch (OperationCanceledException) { return Results.StatusCode(503); }
        });
    }
}

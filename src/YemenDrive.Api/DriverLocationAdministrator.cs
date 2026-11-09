using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using YemenDrive.Api.Tracking;
using YemenDrive.Application.Drivers;
using YemenDrive.Application.Maps;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Api;

public sealed class DriverLocationAdministrator(YemenDriveDbContext db, DriverLocationIngestionService locations,
    GoogleMapsGateway maps) : IDriverLocationAdministrator
{
    private static readonly MemoryCache Routes = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly RideStatus[] ActiveStatuses = [RideStatus.DriverAssigned, RideStatus.DriverEnRoute, RideStatus.DriverArrived, RideStatus.InProgress, RideStatus.CancellationPending];
    private sealed record CachedRoute(int RideId, RideStatus Status, int Version, double Latitude, double Longitude,
        string Polyline, IReadOnlyList<RoutePoint> Points);

    public async Task<object> ExecuteAsync(string operation, DriverLocationAdminModel model, CancellationToken token)
    {
        var gate = Gates[(int)((uint)model.DriverId % (uint)Gates.Length)];
        await gate.WaitAsync(token);
        try
        {
            if (!await db.DriverProfiles.AnyAsync(x => x.UserId == model.DriverId && x.User.IsActive && x.User.Role == UserRole.Driver, token))
                throw new ServiceException("driver_not_found", "السائق غير موجود أو حسابه غير نشط.");
            var ride = await db.Rides.AsNoTracking().Where(x => x.DriverId == model.DriverId && x.ServiceEndedAtUtc == null && ActiveStatuses.Contains(x.Status))
                .OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(token);
            var location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == model.DriverId, token);
            var canMove = ride?.Status is RideStatus.DriverAssigned or RideStatus.DriverEnRoute or RideStatus.InProgress;
            CachedRoute? route = null; RouteAdvance? advance = null; string? routeError = null;
            if (operation == "save")
            {
                if (model.Latitude is not double lat || model.Longitude is not double lng || !double.IsFinite(lat) || !double.IsFinite(lng) ||
                    lat is < -90 or > 90 || lng is < -180 or > 180)
                    throw new ServiceException("invalid_location", "حدد إحداثيات صحيحة على الخريطة.");
                await Save(lat, lng, null, "admin_manual", null);
                Routes.Remove(model.DriverId);
            }
            else if (operation == "move")
            {
                if (!canMove || ride is null) throw new ServiceException("simulation_ride_required", "تجربة التحرك متاحة عندما يكون السائق متجهاً إلى راكب أو في رحلة جارية.");
                if (location is null) throw new ServiceException("location_not_found", "حدد موقع السائق واحفظه أولاً.");
                route = await GetRoute(ride, location, token);
                advance = RouteSimulationGeometry.Advance(route.Points, new(location.Latitude, location.Longitude));
                await Save(advance.Point.Latitude, advance.Point.Longitude, advance.Bearing, "admin_simulation", location.ObservedAtUtc);
            }
            else if (operation != "get") throw new ServiceException("invalid_operation", "عملية غير مدعومة.");
            location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == model.DriverId, token);
            if (route is null && canMove && ride is not null && location is not null)
            {
                try { route = await GetRoute(ride, location, token); }
                catch (ServiceException exception) { routeError = exception.Message; }
            }
            return new { driverId = model.DriverId, location = location is null ? null : new { location.Latitude, location.Longitude, location.Bearing, location.ObservedAtUtc },
                ride = ride is null ? null : new { ride.Id, ride.Status }, canMove, accepted = operation != "get", routePolyline = route?.Polyline, routeError,
                distanceMeters = advance?.DistanceMeters, snapDistanceMeters = advance?.SnapDistanceMeters };

            async Task Save(double latitude, double longitude, double? bearing, string source, DateTime? expected)
            {
                try { await locations.SaveAdministratorAsync(model.DriverId,
                    new(ride?.Id, latitude, longitude, bearing, 0, 1, DateTime.UtcNow), source, expected,
                    source == "admin_simulation" ? ride?.RowVersion : null); }
                catch (HubException exception) { throw new ServiceException("invalid_location", exception.Message); }
            }
        }
        finally { gate.Release(); }
    }

    private async Task<CachedRoute> GetRoute(Ride ride, DriverLiveLocation location, CancellationToken token)
    {
        var latitude = ride.Status == RideStatus.InProgress ? ride.DestinationLatitude : ride.PickupLatitude;
        var longitude = ride.Status == RideStatus.InProgress ? ride.DestinationLongitude : ride.PickupLongitude;
        if (Routes.TryGetValue<CachedRoute>(location.DriverId, out var cached) && cached is not null &&
            cached.RideId == ride.Id && cached.Status == ride.Status && cached.Version == ride.RouteVersion && cached.Latitude == latitude && cached.Longitude == longitude)
            return cached;
        var polyline = ride.Status == RideStatus.InProgress ? ride.RoutePolyline : null;
        if (string.IsNullOrWhiteSpace(polyline))
            polyline = (await maps.ComputeRouteAsync(location.Latitude, location.Longitude, latitude, longitude, false, token)).EncodedPolyline;
        var points = RouteSimulationGeometry.Decode(polyline);
        if (points.Count < 2) throw new ServiceException("simulation_route_missing", "لا يتوفر مسار قيادة صالح لهذه الرحلة.");
        var result = new CachedRoute(ride.Id, ride.Status, ride.RouteVersion, latitude, longitude, polyline, points);
        Routes.Set(location.DriverId, result, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(10) });
        return result;
    }
}

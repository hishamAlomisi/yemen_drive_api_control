using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using YemenDrive.Api;
using YemenDrive.Application.Administration;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

static class AdministrationTrackingScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int driverId, int administratorId)
    {
        var root = new User { PhoneNumber = "967700119999", DisplayName = "System test", Role = UserRole.Admin, IsSystemAdministrator = true };
        db.Users.Add(root); await db.SaveChangesAsync();
        await AdministratorAccess.RequireAsync(db, root.Id, "permissions.write", default);
        Check(!await db.AdministratorPermissions.AnyAsync(x => x.UserId == root.Id), "root authority requires no mutable permission row");
        var rootService = new AdministratorPermissionService(db, config, new TestActor(root.Id));
        var mine = await Run(rootService, "mine", new AdministratorPermissionModel());
        var rootView = JsonSerializer.SerializeToElement(mine.Data);
        Check(mine.Success && rootView.GetProperty("permissions")[0].GetString() == "*" && rootView.GetProperty("canEdit").GetBoolean() == false, "root effective permissions are automatic and protected");
        var denied = await Run(rootService, "update", new AdministratorPermissionModel(root.Id, [], "test protection"));
        Check(!denied.Success && denied.Code == "system_administrator_permissions_protected", "root cannot remove its original authority");
        var delegated = await Run(rootService, "update", new AdministratorPermissionModel(administratorId, ["support.read"], "approved fixture"));
        Check(delegated.Success && await db.AdministratorPermissions.AnyAsync(x => x.UserId == administratorId && x.Permission == "support.read"), "root can manage another administrator");
        var otherService = new AdministratorPermissionService(db, config, new TestActor(administratorId));
        Check(!(await Run(otherService, "update", new AdministratorPermissionModel(root.Id, [], "unauthorized fixture"))).Success, "delegated staff cannot edit root permissions");
        Check((await Run(rootService, "update", new AdministratorPermissionModel(administratorId, ["*"], "restore fixture"))).Success, "fixture permissions restored");
        var identities = new AdminRecordIdentityService(db, config, new TestActor(root.Id));
        var preview = await Run(identities, "get", new AdminRecordIdentityModel("DriverRatingModel"));
        Check(preview.Success, "identity preview query succeeded: " + preview.Code + " " + preview.Message);
        Check(preview.Success && JsonSerializer.SerializeToElement(preview.Data).GetProperty("nextId").GetInt32() == 1, "empty identity table previews first automatic ID");
        var area = new ServiceArea { CountryCode = "YE", CountryNameAr = "اليمن", CityNameAr = "اختبار" };
        db.ServiceAreas.Add(area); await db.SaveChangesAsync();
        preview = await Run(identities, "get", new AdminRecordIdentityModel("ServiceAreaModel"));
        Check(JsonSerializer.SerializeToElement(preview.Data).GetProperty("nextId").GetInt32() == area.Id + 1, "preview follows the authoritative identity sequence");
        var subscriptions = new NearbyDriverSubscriptionStore();
        var realtime = DispatchProxy.Create<IRideRealtimePublisher, CompletedProxy>();
        var locationService = new DriverLocationIngestionService(db, realtime, subscriptions, null!, new TestLifetime(), NullLogger<DriverLocationIngestionService>.Instance);
        var sample = new DriverLocationHubRequest(null, 15.36, 44.19, null, null, 10, DateTime.UtcNow.AddSeconds(-4));
        var http = new DefaultHttpContext(); http.Items[AuthSessionAuthenticationMiddleware.UserIdItem] = driverId;
        var caller = new DisconnectedCaller(http);
        var hub = new TrackingHub(db, new TrackingHubAccessPolicy(db), subscriptions, NullLogger<TrackingHub>.Instance, locationService) { Context = caller };
        await hub.PublishDriverLocation(sample);
        Check(caller.ConnectionAborted.IsCancellationRequested && await db.DriverLiveLocations.AnyAsync(x => x.DriverId == driverId && x.IsOnline), "an accepted GPS sample survives caller disconnect");
        await locationService.SaveAsync(driverId, sample); // Identical Hub/HTTP retries remain idempotent.
        Check(await db.DriverLiveLocations.CountAsync(x => x.DriverId == driverId) == 1, "replayed GPS sample does not duplicate live location");
        await locationService.SaveAsync(driverId, sample with { Latitude = 15.3601, ObservedAtUtc = DateTime.UtcNow.AddSeconds(-2) });
        await locationService.SaveAsync(driverId, sample);
        Check((await db.DriverLiveLocations.SingleAsync(x => x.DriverId == driverId)).Latitude == 15.3601, "old HTTP retry cannot replace a newer fix");
        var rejected = false;
        try { await locationService.SaveAsync(driverId, sample with { Accuracy = 100, ObservedAtUtc = DateTime.UtcNow }); } catch (HubException) { rejected = true; }
        Check(rejected, "GPS fallback retains accuracy validation");
        rejected = false;
        try { await locationService.SaveAsync(root.Id, sample); } catch (HubException) { rejected = true; }
        Check(rejected, "GPS ingestion derives driver eligibility from the authenticated account");
        rejected = false;
        try { await locationService.SaveAsync(driverId, sample with { RideId = int.MaxValue }); } catch (HubException) { rejected = true; }
        Check(rejected, "GPS fallback retains ride ownership validation");
        db.DriverLiveLocations.Remove(await db.DriverLiveLocations.SingleAsync(x => x.DriverId == driverId));
        await db.SaveChangesAsync();
    }
    private static Task<ApiResult> Run<T>(IModelService service, string operation, T model) => service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), default);
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private sealed class TestActor(int id) : ICurrentUserContext { public int? UserId => id; }
    private sealed class TestLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
    public class CompletedProxy : DispatchProxy { protected override object? Invoke(MethodInfo? method, object?[]? args) => Task.CompletedTask; }
    private sealed class HttpFeature(HttpContext context) : IHttpContextFeature { public HttpContext? HttpContext { get; set; } = context; }
    private sealed class DisconnectedCaller : HubCallerContext {
        public DisconnectedCaller(HttpContext context) { Features.Set<IHttpContextFeature>(new HttpFeature(context)); }
        public override string ConnectionId => "isolated-disconnected-driver";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => new(true);
        public override void Abort() { }
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;

internal static class WaitingReportScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int customer, int driver, int admin, int kind, int catalog)
    {
        var ride = new RideEntity { CustomerId = customer, DriverId = driver, ServiceKindId = kind,
            ServiceCatalogItemId = catalog, Status = RideStatus.InProgress, CustomerPrice = 1000,
            WaitingFee = 250, TotalAmount = 1350, WaitingPricePerMinute = 999,
            PickupLabel = "صنعاء - الانطلاق", DestinationLabel = "الوجهة" };
        db.Rides.Add(ride);
        await db.SaveChangesAsync();
        var date = new DateTime(2024, 1, 1, 21, 0, 0, DateTimeKind.Utc); // Jan 2, midnight Yemen.
        RideWaitingSession Closed(int offset, int seconds, decimal fee, string reason) => new()
        {
            RideId = ride.Id, RequestedByUserId = driver, ApprovedByUserId = customer, RequestKey = Guid.NewGuid(),
            CreatedAtUtc = date.AddHours(offset), StartedAtUtc = date.AddHours(offset).AddMinutes(1),
            EndedAtUtc = date.AddHours(offset).AddMinutes(1).AddSeconds(seconds), IsOpen = false, EndReason = reason,
            FreeMinutes = 2, PricePerMinute = 50, MaxFee = reason == "fee_cap" ? 100 : 500, Fee = fee, Latitude = 15.3694, Longitude = 44.191
        };
        var charged = Closed(0, 300, 150, "driver_resumed");
        var capped = Closed(1, 240, 100, "fee_cap");
        var free = Closed(2, 90, 0, "driver_resumed");
        var rejected = Closed(24, 0, 0, "rejected"); rejected.StartedAtUtc = null; rejected.ApprovedByUserId = null;
        var active = new RideWaitingSession { RideId = ride.Id, RequestedByUserId = driver, ApprovedByUserId = customer,
            RequestKey = Guid.NewGuid(), StartedAtUtc = DateTime.UtcNow.AddMinutes(-5), FreeMinutes = 2,
            PricePerMinute = 50, MaxFee = 100, Latitude = 15.4, Longitude = 44.3 };
        db.RideWaitingSessions.AddRange(charged, capped, free, rejected, active);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new RideWaitingReport(db, config, new Actor(admin));
        var filter = new RideWaitingReportModel(RideId: ride.Id, PageSize: 2);
        var report = Data(await Execute(service, filter));
        Is(report.GetProperty("totalCount").GetInt32() == 5 && report.GetProperty("pageCount").GetInt32() == 3, "pagination counts every session");
        Is(report.GetProperty("rows").GetArrayLength() == 2, "page size honored");
        var summary = report.GetProperty("summary");
        Is(summary.GetProperty("chargedFee").GetDecimal() == 250 && summary.GetProperty("rideCount").GetInt32() == 1, "totals are per stop across all pages, not repeated ride fees");
        Is(summary.GetProperty("endedSeconds").GetInt64() == 630 && summary.GetProperty("activeCount").GetInt32() == 1, "summary excludes pending and active duration");
        var current = report.GetProperty("rows")[0];
        Is(current.GetProperty("chargedFee").GetDecimal() == 0 && current.GetProperty("estimatedFee").GetDecimal() == 100, "open stop is only an estimate capped by its own policy");
        Is(await db.RideWaitingSessions.AsNoTracking().AnyAsync(x => x.Id == active.Id && x.IsOpen && x.Fee == 0) && !db.ChangeTracker.HasChanges(), "report cannot reconcile or post charges");

        var day = filter with { PageSize = 100, FromUtc = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.FromHours(3)), ToUtc = new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.FromHours(3)) };
        report = Data(await Execute(service, day));
        Is(report.GetProperty("totalCount").GetInt32() == 3, "period includes start and excludes next midnight with timezone conversion");
        var old = report.GetProperty("rows").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == charged.Id);
        Is(old.GetProperty("pricePerMinute").GetDecimal() == 50 && old.GetProperty("latitude").GetDouble() == 15.3694, "saved rate/location survive changed ride policy and driver location");
        Is(old.GetProperty("durationSeconds").GetInt64() == 300 && old.GetProperty("freeSeconds").GetInt64() == 120 && old.GetProperty("chargeableSeconds").GetInt64() == 180, "free and chargeable duration separated");
        report = Data(await Execute(service, filter with { ChargedOnly = true }));
        Is(report.GetProperty("totalCount").GetInt32() == 2, "charged-only excludes free, rejected, and provisional fees");
        report = Data(await Execute(service, filter with { Status = "unstarted" }));
        Is(report.GetProperty("totalCount").GetInt32() == 1 && report.GetProperty("summary").GetProperty("chargedFee").GetDecimal() == 0, "rejected request has no fees or stop duration");
        report = Data(await Execute(service, filter with { Search = "no-matching-person-721bf" }));
        Is(report.GetProperty("totalCount").GetInt32() == 0 && report.GetProperty("rows").GetArrayLength() == 0, "empty search returns zero summary");
        report = Data(await Execute(service, filter with { Page = 900 }));
        Is(report.GetProperty("page").GetInt32() == 3, "out of date page is clamped");
        Is(!(await Execute(service, day with { FromUtc = day.ToUtc })).Success && !(await Execute(service, filter with { PageSize = 101 })).Success, "invalid period and paging rejected");
        foreach (var operation in new[] { "report", "list", "search" })
        {
            Is(!(await Execute(new RideWaitingReport(db, config, new Actor(customer)), filter, operation)).Success, "customer cannot access " + operation);
            Is(!(await Execute(new RideWaitingReport(db, config, new Actor(driver)), filter, operation)).Success, "driver cannot access " + operation);
            Is(!(await Execute(new RideWaitingReport(db, config, new Actor(null)), filter, operation)).Success, "anonymous cannot access " + operation);
        }
        var adminUser = await db.Users.SingleAsync(x => x.Id == admin);
        adminUser.IsActive = false; await db.SaveChangesAsync();
        try { Is(!(await Execute(service, filter)).Success, "inactive admin denied"); }
        finally { adminUser.IsActive = true; await db.SaveChangesAsync(); }
        Console.WriteLine("PASS: waiting report access, per-stop totals, pagination, date boundaries, policy/location snapshots, estimates and read-only behavior.");
    }
    private static Task<ApiResult> Execute(RideWaitingReport service, RideWaitingReportModel model, string op = "report") => service.ExecuteAsync(op, JsonSerializer.SerializeToElement(model), CancellationToken.None);
    private static JsonElement Data(ApiResult result) { Is(result.Success, result.Code + ": " + result.Message); return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
    private static void Is(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Actor(int? id) : ICurrentUserContext { public int? UserId => id; }
}

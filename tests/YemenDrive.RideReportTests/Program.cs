using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;

var databaseName = "YemenDrive_RideReportTests_" + Guid.NewGuid().ToString("N");
var options = new DbContextOptionsBuilder<YemenDriveDbContext>()
    .UseSqlServer($"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True").Options;
var settingsFile = Path.Combine(Path.GetTempPath(), databaseName + ".json");
var config = new DatabaseConfigurationStore(settingsFile);
await config.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB", Database = databaseName,
    IntegratedSecurity = true, TrustServerCertificate = true }, default);
var checks = 0;
try
{
    await using var db = new YemenDriveDbContext(options);
    await db.Database.MigrateAsync();
    var admin = new User { PhoneNumber = "967700889101", DisplayName = "Report administrator", Role = UserRole.Admin };
    var customer = new User { PhoneNumber = "967700889102", DisplayName = "Report customer", Role = UserRole.Customer };
    var outsider = new User { PhoneNumber = "967700889103", DisplayName = "Other customer", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "967700889104", DisplayName = "Report driver", Role = UserRole.Driver };
    var kind = new ServiceKind { Code = "ride-report", NameAr = "تقرير الرحلات" };
    db.AddRange(admin, customer, outsider, driver, kind); await db.SaveChangesAsync();
    var service = new RideServiceCatalogItem { Code = "ride-report-car", NameAr = "سيارة", ServiceKindId = kind.Id };
    db.Add(service); await db.SaveChangesAsync();
    var createdAtUtc = new DateTime(2026, 11, 5, 16, 32, 19, DateTimeKind.Utc);
    var acceptedAtUtc = createdAtUtc.AddSeconds(37);
    var target = new RideEntity { CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id,
        Status = RideStatus.Completed, CreatedAtUtc = createdAtUtc, UpdatedAtUtc = createdAtUtc.AddMinutes(5),
        ServiceEndedAtUtc = createdAtUtc.AddMinutes(4), CompletedAtUtc = createdAtUtc.AddMinutes(5),
        PassengerBoardedAtUtc = createdAtUtc.AddMinutes(1) };
    var legacy = new RideEntity { CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id,
        Status = RideStatus.Completed, CreatedAtUtc = createdAtUtc.AddSeconds(1) };
    db.AddRange(target, legacy); await db.SaveChangesAsync();
    db.RideOffers.Add(new() { RideId = target.Id, DriverId = driver.Id, Status = OfferStatus.Accepted, Amount = 100,
        UpdatedAtUtc = acceptedAtUtc.AddMinutes(1), ExpiresAtUtc = acceptedAtUtc.AddMinutes(2) });
    db.RideMovementSnapshots.Add(new() { RideId = target.Id, RequestedAtUtc = createdAtUtc, AcceptedAtUtc = acceptedAtUtc });
    for (var i = 0; i < 205; i++)
        db.Rides.Add(new() { CustomerId = outsider.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id,
            Status = RideStatus.Cancelled, CreatedAtUtc = createdAtUtc.AddMinutes(10) });
    await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    var adminReport = new RideReport(db, config, new Actor(admin.Id));
    var defaultRows = Ok(await Run(adminReport, new { }));
    Check(defaultRows.ValueKind == JsonValueKind.Array && defaultRows.GetArrayLength() == 200, "existing clients retain bounded array response");
    Check(!defaultRows.EnumerateArray().Any(x => x.GetProperty("id").GetInt32() == target.Id), "old target is genuinely beyond default 200-row window");
    var first = Ok(await Run(adminReport, new { includePaging = true, pageNumber = 1, pageSize = 100 }));
    var second = Ok(await Run(adminReport, new { includePaging = true, pageNumber = 2, pageSize = 100 }));
    var last = Ok(await Run(adminReport, new { includePaging = true, pageNumber = 99, pageSize = 100 }));
    Check(first.GetProperty("totalCount").GetInt32() == 207 && last.GetProperty("pageNumber").GetInt32() == 3 &&
        last.GetProperty("rows").GetArrayLength() == 7, "admin pages expose full count and clamp past final page");
    var allRows = first.GetProperty("rows").EnumerateArray().Concat(second.GetProperty("rows").EnumerateArray())
        .Concat(last.GetProperty("rows").EnumerateArray()).ToArray();
    Check(allRows.Select(x => x.GetProperty("id").GetInt32()).Distinct().Count() == 207, "stable ID ordering visits tied-date rows without duplicates or omissions");
    var exact = Ok(await Run(adminReport, new { includePaging = true, id = target.Id }));
    var row = exact.GetProperty("rows")[0];
    Check(exact.GetProperty("totalCount").GetInt32() == 1 && row.GetProperty("id").GetInt32() == target.Id, "exact number search finds older completed ride");
    Check(row.GetProperty("acceptedAtUtc").GetDateTime() == acceptedAtUtc && row.GetProperty("acceptedAtUtc").GetString()!.EndsWith('Z'),
        "agreement uses immutable snapshot and explicit UTC instead of later offer edit");
    foreach (var name in new[] { "createdAtUtc", "updatedAtUtc", "serviceEndedAtUtc", "completedAtUtc", "passengerBoardedAtUtc" })
        Check(row.GetProperty(name).GetString()!.EndsWith('Z'), $"{name} is explicitly UTC");
    var missing = Ok(await Run(adminReport, new { includePaging = true, id = legacy.Id })).GetProperty("rows")[0];
    Check(missing.GetProperty("acceptedAtUtc").ValueKind == JsonValueKind.Null, "legacy missing agreement remains unknown");
    var completed = Ok(await Run(adminReport, new { includePaging = true, status = (int)RideStatus.Completed }));
    Check(completed.GetProperty("totalCount").GetInt32() == 2, "status filtering precedes pagination and count");
    var absent = Ok(await Run(adminReport, new { includePaging = true, id = int.MaxValue }));
    Check(absent.GetProperty("totalCount").GetInt32() == 0 && absent.GetProperty("rows").GetArrayLength() == 0 &&
        absent.GetProperty("totalPages").GetInt32() == 1, "unknown exact number returns explicit empty page");
    foreach (var request in new object[] { new { includePaging = true, pageNumber = 0 }, new { includePaging = true, pageNumber = 1_000_001 },
        new { includePaging = true, pageSize = 101 }, new { includePaging = true, pageSize = 0 }, new { includePaging = true, id = -1 } })
        Check((await Run(adminReport, request)).Code == "invalid_ride_report_filter", "invalid filter is rejected");
    Check((await Run(new RideReport(db, config, new Actor(customer.Id)), new { includePaging = true })).Code == "administrator_permission_required",
        "customer cannot request administrator paging");
    var own = Ok(await Run(new RideReport(db, config, new Actor(customer.Id)), new { }));
    Check(own.GetArrayLength() == 2 && own.EnumerateArray().All(x => x.GetProperty("customerId").GetInt32() == customer.Id),
        "existing customer list remains participant scoped");
    Check((await Run(new RideReport(db, config, new Actor(null)), new { includePaging = true })).Code == "administrator_permission_required",
        "anonymous paging is denied");
    Console.WriteLine($"PASS: {checks} ride report checks on isolated LocalDB.");
}
finally
{
    if (!databaseName.StartsWith("YemenDrive_RideReportTests_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup database.");
    await using var cleanup = new YemenDriveDbContext(options); await cleanup.Database.EnsureDeletedAsync();
    File.Delete(settingsFile);
}

void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
JsonElement Ok(ApiResult result) { if (!result.Success) throw new Exception(result.Code + ": " + result.Message); return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
static Task<ApiResult> Run(RideReport report, object model) => report.ExecuteAsync("list", JsonSerializer.SerializeToElement(model, new JsonSerializerOptions(JsonSerializerDefaults.Web)), default);
public sealed record Actor(int? UserId) : ICurrentUserContext;

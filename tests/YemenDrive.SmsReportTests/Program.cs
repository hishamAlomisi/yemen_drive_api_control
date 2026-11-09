using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YemenDrive.Api.Sms;
using YemenDrive.Application.Communication;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

if (args.Contains("--runtime-preflight") || args.Contains("--apply-runtime-migration"))
{
    await RuntimeMigration.RunAsync(args.Contains("--apply-runtime-migration"));
    return;
}
var checks = 0;
var name = "YemenDrive_SmsReportTests_" + Guid.NewGuid().ToString("N");
var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={name};Integrated Security=True;TrustServerCertificate=True";
var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
WebApplication? app = null;
try
{
    await using var db = new YemenDriveDbContext(options);
    await db.Database.MigrateAsync();
    Check(!db.Database.HasPendingModelChanges(), "migration snapshot matches current model");
    var statistics = new SmsStatisticsService(db);
    var empty = await statistics.GetAsync(default);
    Check(empty.TotalCount == 0 && empty.SignInCount == 0 && empty.PasswordResetCount == 0 && empty.RegistrationCount == 0 && empty.LastSentAtUtc is null, "empty report has zero counts");
    var sent = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
    foreach (var purpose in Enum.GetValues<SmsMessagePurpose>().Where(x => x != SmsMessagePurpose.Manual))
        for (var i = 0; i < (int)purpose; i++)
            Check(await statistics.RecordSentAsync($"synthetic-{purpose}-{i}", purpose, sent.AddMinutes(i), default), "message recorded");
    var result = await statistics.GetAsync(default);
    Check(result.SignInCount == 1 && result.PasswordResetCount == 2 && result.RegistrationCount == 3 && result.WalletMovementCount == 4 && result.OtherOtpCount == 5 && result.TotalCount == 15, "all categories and total agree");
    Check(result.LastSentAtUtc == sent.AddMinutes(4) && result.LastSentAtUtc.Value.Kind == DateTimeKind.Utc, "latest timestamp is UTC");
    Check(!await statistics.RecordSentAsync("synthetic-SignIn-0", SmsMessagePurpose.SignIn, sent.AddDays(1), default), "replayed message is not counted again");
    var parallel = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
    {
        await using var concurrent = new YemenDriveDbContext(options);
        return await new SmsStatisticsService(concurrent).RecordSentAsync("synthetic-concurrent", SmsMessagePurpose.SignIn, sent, default);
    }));
    Check(parallel.Count(x => x) == 1 && (await statistics.GetAsync(default)).TotalCount == 16, "concurrent replay records once");
    try { await statistics.RecordSentAsync("synthetic-SignIn-0", SmsMessagePurpose.PasswordReset, sent, default); throw new Exception("Expected conflict"); }
    catch (ServiceException exception) { Check(exception.Code == "sms_record_conflict", "conflicting purpose is rejected"); }
    try { await statistics.RecordSentAsync("synthetic-invalid", (SmsMessagePurpose)7, sent, default); throw new Exception("Expected invalid record"); }
    catch (ServiceException exception) { Check(exception.Code == "invalid_sms_record", "invalid category is rejected"); }
    await using (var reopened = new YemenDriveDbContext(options))
        Check((await new SmsStatisticsService(reopened).GetAsync(default)).TotalCount == 16, "counts persist across new service/context");
    Check(await statistics.RecordSentAsync("synthetic-manual-bulk", SmsMessagePurpose.Manual, sent, default, 5), "bulk accepted recipient count is recorded");
    Check((await statistics.GetAsync(default)).ManualCount == 5 && (await statistics.GetAsync(default)).TotalCount == 21, "manual recipients counted separately and in total");
    try { await statistics.RecordSentAsync("synthetic-manual-bulk", SmsMessagePurpose.Manual, sent, default, 4); throw new Exception("Expected count conflict"); }
    catch (ServiceException exception) { Check(exception.Code == "sms_record_conflict", "replay cannot change recipient count"); }

    var customer = new User { PhoneNumber = "967700889811", Role = UserRole.Customer };
    var denied = new User { PhoneNumber = "967700889812", Role = UserRole.Admin };
    var reader = new User { PhoneNumber = "967700889813", Role = UserRole.Admin };
    db.Users.AddRange(customer, denied, reader); await db.SaveChangesAsync();
    db.AdministratorPermissions.Add(new() { UserId = reader.Id, Permission = "operations.read" }); await db.SaveChangesAsync();
    var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddDbContext<YemenDriveDbContext>(setup => setup.UseSqlServer(connection));
    builder.Services.AddScoped<SmsStatisticsService>(); builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserContext, TestActor>();
    app = builder.Build(); app.MapSmsStatisticsEndpoints(); await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    using (var response = await http.GetAsync("/api/admin/sms-statistics"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true, "anonymous request is rejected without caching");
    foreach (var actor in new[] { customer, denied })
    {
        http.DefaultRequestHeaders.Remove("X-Test-Actor"); http.DefaultRequestHeaders.Add("X-Test-Actor", actor.Id.ToString());
        using var response = await http.GetAsync("/api/admin/sms-statistics");
        Check(response.StatusCode == HttpStatusCode.Forbidden, "customer or unauthorized administrator cannot read report");
    }
    http.DefaultRequestHeaders.Remove("X-Test-Actor"); http.DefaultRequestHeaders.Add("X-Test-Actor", reader.Id.ToString());
    using (var response = await http.GetAsync("/api/admin/sms-statistics"))
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true && data.GetProperty("totalCount").GetInt64() == 21, "authorized reader receives actual counts");
        Check(data.EnumerateObject().Count() == 8 && !data.ToString().Contains("synthetic"), "report contains aggregate counts only");
    }
    Console.WriteLine($"PASS: {checks} SMS report checks; migrated disposable LocalDB, no SMS or FCM sent.");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    if (!name.StartsWith("YemenDrive_SmsReportTests_", StringComparison.Ordinal)) throw new Exception("Unsafe test cleanup target");
    await using var cleanup = new YemenDriveDbContext(options); await cleanup.Database.EnsureDeletedAsync();
}
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }

sealed class TestActor(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor) : ICurrentUserContext
{
    public int? UserId => int.TryParse(accessor.HttpContext?.Request.Headers["X-Test-Actor"].ToString(), out var id) ? id : null;
}

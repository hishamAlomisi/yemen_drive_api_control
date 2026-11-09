using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YemenDrive.Api.Sms;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

var checks = 0;
var testName = "YemenDrive_SmsProviderTests_" + Guid.NewGuid().ToString("N");
var testRoot = Path.Combine(Path.GetTempPath(), testName);
Directory.CreateDirectory(testRoot);
var keys = new DirectoryInfo(Path.Combine(testRoot, "keys"));
var provider = DataProtectionProvider.Create(keys);
var file = Path.Combine(testRoot, "sms-provider.dat");
var store = new SmsProviderConfigurationStore(provider, file);
var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={testName};Integrated Security=True;TrustServerCertificate=True";
var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
WebApplication? app = null;
try
{
    Check(!(await store.GetAsync(default)).HasPassword, "fresh configuration is empty");
    await Reject(new("https://example.test/sms", "user", null, "0012"), "sms_password_required");
    await Reject(new("javascript:alert(1)", "user", "secret", "0012"), "invalid_sms_url");
    await Reject(new("https://example.test/sms", " ", "secret", "0012"), "invalid_sms_username");
    await Reject(new("https://example.test/sms", "user", "secret", " "), "invalid_sms_device");
    var saved = await store.SaveAsync(new(" https://example.test/sms ", " مزود تجريبي ", "synthetic-secret-01", "0012"), default);
    Check(saved.DeviceNumber == "0012" && saved.Username == "مزود تجريبي" && saved.HasPassword, "text fields round trip and leading zeros survive");
    Check(!File.ReadAllText(file).Contains("synthetic-secret-01"), "password is encrypted at rest");
    Check(!JsonSerializer.Serialize(saved).Contains("synthetic-secret-01"), "public response excludes password");
    var reopened = new SmsProviderConfigurationStore(DataProtectionProvider.Create(keys), file);
    Check(await reopened.GetAsync(default) == saved, "persisted key ring and configuration survive restart");
    await reopened.SaveAsync(new("https://example.test/new", "user", "", "0008"), default);
    using (var decrypted = JsonDocument.Parse(provider.CreateProtector("YemenDrive.SmsProvider.v1").Unprotect(File.ReadAllText(file))))
        Check(decrypted.RootElement.GetProperty("Password").GetString() == "synthetic-secret-01", "empty password preserves existing value");
    await reopened.SaveAsync(new("https://example.test/new", "user", "synthetic-secret-02", "0008"), default);
    using (var decrypted = JsonDocument.Parse(provider.CreateProtector("YemenDrive.SmsProvider.v1").Unprotect(File.ReadAllText(file))))
        Check(decrypted.RootElement.GetProperty("Password").GetString() == "synthetic-secret-02", "explicit password replaces previous value");
    var before = File.ReadAllText(file);
    await Reject(new("https://example.test/sms", "user", new string('a', 1025), "1"), "invalid_sms_password");
    Check(File.ReadAllText(file) == before, "invalid writes preserve saved configuration");
    File.WriteAllText(file, "corrupted-ciphertext");
    await Reject(new("https://example.test/sms", "user", "secret", "1"), "sms_configuration_unreadable");
    Check(File.ReadAllText(file) == "corrupted-ciphertext", "unreadable data is not silently replaced");
    File.WriteAllText(file, before);

    await using var seed = new YemenDriveDbContext(options);
    await seed.Database.EnsureCreatedAsync();
    var customer = new User { PhoneNumber = "967700889801", Role = UserRole.Customer };
    var reader = new User { PhoneNumber = "967700889802", Role = UserRole.Admin };
    var admin = new User { PhoneNumber = "967700889803", Role = UserRole.Admin, IsSystemAdministrator = true };
    seed.Users.AddRange(customer, reader, admin);
    await seed.SaveChangesAsync();
    seed.AdministratorPermissions.Add(new() { UserId = reader.Id, Permission = "policy.read" });
    await seed.SaveChangesAsync();
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(store);
    builder.Services.AddDbContext<YemenDriveDbContext>(db => db.UseSqlServer(connection));
    builder.Services.AddHttpContextAccessor();
    // Only this isolated host trusts a synthetic actor header; production uses authenticated sessions.
    builder.Services.AddScoped<ICurrentUserContext, TestActor>();
    app = builder.Build();
    app.MapSmsProviderEndpoints();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    var input = new SmsProviderInput("https://example.test/sms", "user", "synthetic-secret-03", "0010");
    using (var response = await http.GetAsync("/api/admin/sms-provider"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true, "anonymous read is rejected without caching");
    using (var response = await http.PostAsJsonAsync("/api/admin/sms-provider", input))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous write is rejected");
    http.DefaultRequestHeaders.Add("X-Test-Actor", customer.Id.ToString());
    using (var response = await http.GetAsync("/api/admin/sms-provider"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "customer cannot access settings");
    http.DefaultRequestHeaders.Remove("X-Test-Actor");
    http.DefaultRequestHeaders.Add("X-Test-Actor", reader.Id.ToString());
    using (var response = await http.GetAsync("/api/admin/sms-provider"))
        Check(response.IsSuccessStatusCode && !(await response.Content.ReadAsStringAsync()).Contains("synthetic-secret"), "reader receives metadata without password");
    using (var response = await http.PostAsJsonAsync("/api/admin/sms-provider", input))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "reader cannot change settings");
    http.DefaultRequestHeaders.Remove("X-Test-Actor");
    http.DefaultRequestHeaders.Add("X-Test-Actor", admin.Id.ToString());
    using (var response = await http.PostAsJsonAsync("/api/admin/sms-provider", input))
        Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true && !(await response.Content.ReadAsStringAsync()).Contains(input.Password!), "administrator can save without password disclosure");
    using (var response = await http.PostAsJsonAsync("/api/admin/sms-provider", input with { DeviceNumber = "" }))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "endpoint exposes safe validation failure");
    Check((await store.GetAsync(default)).DeviceNumber == "0010", "rejected HTTP mutation preserves values");
    Console.WriteLine($"PASS: {checks} SMS provider checks; disposable LocalDB, no provider calls.");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    await using var cleanup = new YemenDriveDbContext(options);
    await cleanup.Database.EnsureDeletedAsync();
    var resolvedRoot = Path.GetFullPath(testRoot);
    if (!resolvedRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(resolvedRoot) != testName) throw new InvalidOperationException("Unsafe temporary cleanup path");
    Directory.Delete(resolvedRoot, recursive: true);
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}
async Task Reject(SmsProviderInput input, string code)
{
    try { await store.SaveAsync(input, default); throw new InvalidOperationException("Expected rejection: " + code); }
    catch (ServiceException exception) { Check(exception.Code == code, code); }
}

sealed class TestActor(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor) : ICurrentUserContext
{
    public int? UserId => int.TryParse(accessor.HttpContext?.Request.Headers["X-Test-Actor"].ToString(), out var id) ? id : null;
}

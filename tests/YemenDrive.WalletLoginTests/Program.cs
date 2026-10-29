using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using eqsWallets;
using eqsWallets.Providers.Floosak;
using eqsWallets.Providers.Jaib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Api.WalletLogin;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using PaymentMethodService = YemenDrive.Application.Payments.PaymentMethod;
using PaymentMethodEntity = YemenDrive.Database.Entities.PaymentMethod;

var catalog = new WalletProviderCatalog();
Check(catalog.Providers.Count == 7, "seven provider registrations");
var expectedTemplateFields = new Dictionary<string, string>
{
    ["Jaib"] = "BaseAddress Environment AgentCode ClientId ClientSecret UserName Password CertificatePath ProviderGrant AgentClientSecret ExpiryUnit RemittanceSendRoute",
    ["Jawali"] = "BaseAddress Environment ApiUserName ApiPassword AgentWallet WalletPassword OrganizationId ExternalUser PointOfServiceId MemberId StatementEmail OAuthClientId OAuthClientSecret ServiceClientId",
    ["Floosak"] = "BaseAddress Environment PhoneNumber Password CashInRoute",
    ["Cash"] = "BaseAddress Environment UserName Password",
    ["Speed"] = "BaseAddress Environment ClientId ClientSecret AgentCode BranchCode UserName Password AgentUserName Language",
    ["MobileMoney"] = "BaseAddress Environment UserName Password ClientId",
    ["YeahMoney"] = "BaseAddress Environment ClientId ClientSecret AgentCode BranchCode UserName Password AgentUserName Language"
};
foreach (var definition in catalog.Providers)
{
    var template = JsonNode.Parse(definition.TemplateJson)!.AsObject();
    Check(template.Select(x => x.Key).SequenceEqual(expectedTemplateFields[definition.Code].Split(' ')),
        "only the requested login fields in the requested order for " + definition.Code);
    var json = FilledTemplate(definition);
    var parsed = catalog.Parse(definition.Code.ToLowerInvariant(), json);
    Check(parsed.GetType().Name == definition.Code + "Options", "typed options " + definition.Code);
    Check(!definition.TemplateJson.Contains("ResponseReceived") && !definition.TemplateJson.Contains("WorkflowStore"), "no callbacks in template");
    var defaults = Activator.CreateInstance(parsed.GetType())!;
    foreach (var name in new[] { "RequestTimeout", "TokenRenewalSkew", "ConfirmationLifetime", "CertificateKeyStorageFlags", "ApplicationName", "ApplicationVersion" })
    {
        var property = parsed.GetType().GetProperty(name);
        if (property is not null)
        {
            var expected = definition.Code == "Jaib" && name == "CertificateKeyStorageFlags" && OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.UserKeySet : property.GetValue(defaults);
            Check(Equals(property.GetValue(parsed), expected),
                "omitted option keeps its configured default: " + definition.Code + "." + name);
        }
    }
}
Reject(() => catalog.Parse("Floosak", "{\"Password\":\"hidden-value\"}"), "wallet_options_invalid");
Reject(() => catalog.Parse("Unknown", "{}"), "wallet_provider_invalid");
var baseJson = FilledTemplate(catalog.Find("Floosak"));
Reject(() => catalog.Parse("Floosak", baseJson[..^1] + ",\"password\":\"duplicate\"}"), "wallet_options_invalid");
Reject(() => catalog.Parse("Floosak", baseJson[..^1] + ",\"ResponseReceived\":null}"), "wallet_options_invalid");
Reject(() => catalog.Parse("Floosak", "[]"), "wallet_options_invalid");
Reject(() => catalog.Parse("Floosak", "{\"x\":\"" + new string('x', 32768) + "\"}"), "wallet_options_invalid");
var maps = JsonNode.Parse(baseJson)!.AsObject();
maps["SourceWallets"] = JsonNode.Parse("{\"YER\":12345,\"SAR\":67890}");
maps["TopUpProducts"] = JsonNode.Parse("{\"product-ref\":{\"Method\":1,\"Bunch\":2}}");
var floosak = (FloosakOptions)catalog.Parse("Floosak", maps.ToJsonString());
Check(floosak.SourceWallets[CurrencyCode.YER].Value == 12345, "currency dictionary converter");
Check(floosak.TopUpProducts[new WalletReference("product-ref")].Bunch.Value == 2, "reference/id converters");
Console.WriteLine("PASS: seven minimal typed templates, exact field order, configured defaults, optional maps, duplicate/unknown fields and input limits.");

var jaibJson = JsonNode.Parse(FilledTemplate(catalog.Find("Jaib")))!.AsObject();
jaibJson["CertificatePath"] = @"D:\path\sss.pfx";
var escapedPathJson = jaibJson.ToJsonString();
var jaib = (JaibOptions)catalog.Parse("Jaib", escapedPathJson);
Check(jaib.CertificatePath == @"D:\path\sss.pfx", "escaped Windows certificate path round-trips without losing backslashes");
Check(jaib.CertificateKeyStorageFlags == (OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : new JaibOptions().CertificateKeyStorageFlags),
    "Jaib omitted certificate flags use user-scoped key storage on Windows only");
Reject(() => catalog.Parse("Jaib", escapedPathJson.Replace(@"D:\\path\\sss.pfx", @"D:\path\sss.pfx", StringComparison.Ordinal)),
    "wallet_options_invalid");
jaibJson["CertificatePath"] = "D:/path/sss.pfx";
Check(((JaibOptions)catalog.Parse("Jaib", jaibJson.ToJsonString())).CertificatePath == "D:/path/sss.pfx",
    "forward-slash certificate path remains valid JSON");
foreach (var explicitFlags in new[] { X509KeyStorageFlags.DefaultKeySet, X509KeyStorageFlags.EphemeralKeySet,
    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet })
{
    jaibJson["certificatekeystorageflags"] = explicitFlags.ToString();
    Check(((JaibOptions)catalog.Parse("Jaib", jaibJson.ToJsonString())).CertificateKeyStorageFlags == explicitFlags,
        "explicit Jaib certificate flags are honored case-insensitively: " + explicitFlags);
}
Console.WriteLine("PASS: Jaib user-scoped certificate default, explicit overrides, and valid/invalid JSON certificate paths.");

var tempRoot = Path.Combine(Path.GetTempPath(), "YemenDriveWalletTests_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
var keyRing = new DirectoryInfo(Path.Combine(tempRoot, "keys"));
var provider = DataProtectionProvider.Create(keyRing, options => options.SetApplicationName("WalletTests"));
var protector = new WalletCredentialsProtector(provider);
var encrypted = protector.Protect("Floosak", baseJson);
Check(!encrypted.Contains("fixture-password"), "no plaintext ciphertext");
var restartedProtector = new WalletCredentialsProtector(DataProtectionProvider.Create(keyRing,
    options => options.SetApplicationName("WalletTests")));
Check(restartedProtector.Unprotect("floosak", encrypted) == baseJson, "persistent key ring survives restart");
try { restartedProtector.Unprotect("Jaib", encrypted); throw new Exception("Expected different provider purpose failure"); }
catch (System.Security.Cryptography.CryptographicException) { }
Console.WriteLine("PASS: protected credentials survive restart and cannot be read with a different provider purpose.");

var databaseName = "YemenDrive_WalletTests_" + Guid.NewGuid().ToString("N");
var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
var configuration = new DatabaseConfigurationStore(Path.Combine(tempRoot, "database.json"));
await configuration.SaveAsync(new DatabaseSettings
{
    Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB", Database = databaseName,
    IntegratedSecurity = true, TrustServerCertificate = true
}, CancellationToken.None);
var configurationService = new WalletLoginConfigurationService(catalog, protector);
var factory = new RecordingFactory();
var services = new ServiceCollection().AddDbContext<YemenDriveDbContext>(o => o.UseSqlServer(connection)).BuildServiceProvider();
await using var runtime = new WalletLoginRuntime(services.GetRequiredService<IServiceScopeFactory>(), factory,
    catalog, protector, NullLogger<WalletLoginRuntime>.Instance);
try
{
    await using var db = new YemenDriveDbContext(options);
    await db.Database.MigrateAsync();
    var admin = new User { PhoneNumber = "700000091", DisplayName = "Fixture Admin", PasswordHash = "fixture", Role = UserRole.Admin, IsActive = true };
    var customer = new User { PhoneNumber = "700000092", DisplayName = "Fixture Customer", PasswordHash = "fixture", Role = UserRole.Customer, IsActive = true };
    db.Users.AddRange(admin, customer);
    await db.SaveChangesAsync();
    var methodService = new PaymentMethodService(db, configuration, new CurrentUser(admin.Id),
        new FinancialAccountProvisioningService(db), configurationService);
    var model = new PaymentMethodModel(Code: "fixture-floosak", NameAr: "فلوسك تجريبية", DescriptionAr: "fixture",
        ProviderCode: "Floosak", ProviderOptionsJson: baseJson, ProviderLoginEnabled: true);
    var result = await methodService.ExecuteAsync("add", JsonSerializer.SerializeToElement(model), CancellationToken.None);
    Check(result.Success, "admin method add with credentials: " + result.Code);
    var methodId = Convert.ToInt32(result.Data!.GetType().GetProperty("Id")!.GetValue(result.Data));
    var serializedResult = JsonSerializer.Serialize(result);
    Check(!serializedResult.Contains("fixture-password") && !serializedResult.Contains("CredentialsEncrypted") && !serializedResult.Contains("ProviderOptionsJson"), "public result does not expose credentials");
    db.ChangeTracker.Clear();
    var account = await db.PaymentProviderAccounts.SingleAsync();
    Check(account.PaymentMethodId == methodId && account.IsEnabled && account.LoginStatus == "Pending", "atomic linked configuration");
    Check(!account.CredentialsEncrypted.Contains("fixture-password") && protector.Unprotect("Floosak", account.CredentialsEncrypted) == baseJson, "encrypted database record");
    var version = account.ConfigurationVersion;
    var savedCiphertext = account.CredentialsEncrypted;
    var update = model with { Id = methodId, ProviderOptionsJson = null, ProviderLoginEnabled = null, NameAr = "اسم معدّل" };
    Check((await methodService.ExecuteAsync("update", JsonSerializer.SerializeToElement(update), CancellationToken.None)).Success, "ordinary edit");
    db.ChangeTracker.Clear();
    account = await db.PaymentProviderAccounts.SingleAsync();
    Check(account.ConfigurationVersion == version && account.CredentialsEncrypted == savedCiphertext && account.IsEnabled, "blank edit preserves credentials and version");
    var reorderedJson = new JsonObject(JsonNode.Parse(baseJson)!.AsObject().Reverse()
        .Select(x => KeyValuePair.Create(x.Key, x.Value?.DeepClone()))).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    Check((await methodService.ExecuteAsync("update", JsonSerializer.SerializeToElement(update with { ProviderOptionsJson = reorderedJson }), CancellationToken.None)).Success,
        "prepopulated edit accepts unchanged credentials with different formatting and property order");
    db.ChangeTracker.Clear();
    account = await db.PaymentProviderAccounts.SingleAsync();
    Check(account.ConfigurationVersion == version && account.CredentialsEncrypted == savedCiphertext && account.IsEnabled,
        "unchanged prepopulated edit preserves ciphertext and login revision");
    var forbidden = new PaymentMethodService(db, configuration, new CurrentUser(customer.Id), new FinancialAccountProvisioningService(db), configurationService);
    Check((await forbidden.ExecuteAsync("update", JsonSerializer.SerializeToElement(update), CancellationToken.None)).Code == "admin_required", "non-admin cannot configure wallet");

    await Task.WhenAll(runtime.ReconcileAsync(CancellationToken.None), runtime.ReconcileAsync(CancellationToken.None));
    Check(factory.Clients.Count == 1 && factory.Clients[0].Calls == 1, "concurrent/repeated reconciliation logs in once");
    db.ChangeTracker.Clear();
    account = await db.PaymentProviderAccounts.SingleAsync();
    Check(account.LoginStatus == "Ready" && account.LastLoginAtUtc != null, "successful status persisted");
    var successfulLoginAt = account.LastLoginAtUtc;
    Check((await methodService.ExecuteAsync("update", JsonSerializer.SerializeToElement(update with { ProviderOptionsJson = reorderedJson }), CancellationToken.None)).Success,
        "unchanged prepopulated edit after login");
    await runtime.ReconcileAsync(CancellationToken.None);
    db.ChangeTracker.Clear();
    account = await db.PaymentProviderAccounts.SingleAsync();
    Check(account.ConfigurationVersion == version && account.LoginStatus == "Ready" && account.LastLoginAtUtc == successfulLoginAt &&
        factory.Clients.Count == 1 && factory.Clients[0].Calls == 1, "unchanged credentials retain successful session without login retry");

    var second = new PaymentMethodEntity { Code = "fixture-floosak-2", NameAr = "second", DescriptionAr = "fixture", ProviderCode = "Floosak", Kind = PaymentMethodKind.ExternalWallet, IsActive = true };
    await configurationService.ConfigureAsync(db, second, model, CancellationToken.None);
    db.PaymentMethods.Add(second);
    await db.SaveChangesAsync();
    factory.NextSuccess = false;
    await runtime.ReconcileAsync(CancellationToken.None);
    await runtime.ReconcileAsync(CancellationToken.None);
    Check(factory.Clients.Count == 2 && factory.Clients[1].Calls == 1 && factory.Clients[1].Disposed, "second account isolated and failed login not repeated");
    db.ChangeTracker.Clear();
    var secondAccount = await db.PaymentProviderAccounts.SingleAsync(x => x.PaymentMethodId == second.Id);
    Check(secondAccount.LoginStatus == "Failed" && secondAccount.LastErrorCode == "wallet_login_failed", "failure status contains safe code");
    factory.NextSuccess = true;
    secondAccount.ConfigurationVersion = Guid.NewGuid();
    await db.SaveChangesAsync();
    await runtime.ReconcileAsync(CancellationToken.None);
    Check(factory.Clients.Count == 3 && runtime.GetStatus(second.Id, secondAccount.ConfigurationVersion) == "Ready", "new revision retries once");

    db.ChangeTracker.Clear();
    var firstMethod = await db.PaymentMethods.SingleAsync(x => x.Id == methodId);
    await configurationService.ConfigureAsync(db, firstMethod, model with { ProviderOptionsJson = null, ProviderLoginEnabled = false }, CancellationToken.None);
    await db.SaveChangesAsync();
    await runtime.ReconcileAsync(CancellationToken.None);
    Check(factory.Clients[0].Disposed, "disabling configuration disposes session");
    db.ChangeTracker.Clear();
    account = await db.PaymentProviderAccounts.SingleAsync(x => x.PaymentMethodId == methodId);
    Check(account.LoginStatus == "Disabled", "disabled status");

    await VerifyEndpointsAsync(connection, catalog, runtime, protector, baseJson, admin.Id, customer.Id, second.Id, methodId);
    Console.WriteLine("PASS: admin configuration, encrypted atomic persistence, blank edits, account isolation, concurrent login, failures, revision retry, disabled cleanup and endpoint access.");
}
finally
{
    await runtime.DisposeAsync();
    await services.DisposeAsync();
    await using var cleanup = new YemenDriveDbContext(options);
    await cleanup.Database.EnsureDeletedAsync();
    // All files here were created by this test under its randomized temporary directory.
    Directory.Delete(tempRoot, recursive: true);
}

static string FilledTemplate(WalletProviderDefinition definition)
{
    var node = JsonNode.Parse(definition.TemplateJson)!.AsObject();
    foreach (var key in node.Select(x => x.Key).ToArray())
        if (node[key] is JsonValue value && value.TryGetValue<string>(out var text) && text == "")
            node[key] = key == "BaseAddress" ? "https://wallet.example.test/" : key == "PhoneNumber" ? "777000091" : key == "Password" ? "fixture-password" : "fixture-value";
    return node.ToJsonString();
}

static async Task VerifyEndpointsAsync(string connection, WalletProviderCatalog catalog, WalletLoginRuntime runtime,
    WalletCredentialsProtector protector, string expectedJson, int adminId, int customerId, int methodId, int disabledMethodId)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddDbContext<YemenDriveDbContext>(o => o.UseSqlServer(connection));
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserContext, HeaderCurrentUser>();
    builder.Services.AddSingleton(catalog);
    builder.Services.AddSingleton(runtime);
    builder.Services.AddSingleton(protector);
    await using var app = builder.Build();
    app.MapWalletLoginEndpoints();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    var configurationPath = $"/api/admin/payment-methods/{methodId}/wallet-login/configuration";
    Check((await http.GetAsync("/api/admin/wallet-providers")).StatusCode == HttpStatusCode.Unauthorized, "unauthenticated catalog rejected");
    var anonymousConfiguration = await http.GetAsync(configurationPath);
    Check(anonymousConfiguration.StatusCode == HttpStatusCode.Unauthorized && anonymousConfiguration.Headers.CacheControl?.NoStore == true,
        "unauthenticated configuration rejected with no-store");
    http.DefaultRequestHeaders.Add("X-Fixture-User", customerId.ToString());
    Check((await http.GetAsync(configurationPath)).StatusCode == HttpStatusCode.Unauthorized, "customer configuration rejected");
    Check((await http.GetAsync($"/api/admin/payment-methods/{methodId}/wallet-login")).StatusCode == HttpStatusCode.Unauthorized, "customer status rejected");
    Check((await http.PostAsync($"/api/admin/payment-methods/{methodId}/wallet-login/retry", null)).StatusCode == HttpStatusCode.Unauthorized, "customer retry rejected");
    http.DefaultRequestHeaders.Remove("X-Fixture-User");
    http.DefaultRequestHeaders.Add("X-Fixture-User", adminId.ToString());
    var response = await http.GetAsync("/api/admin/wallet-providers");
    Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true, "admin catalog no-store");
    var status = await http.GetStringAsync($"/api/admin/payment-methods/{methodId}/wallet-login");
    Check(status.Contains("Ready") && !status.Contains("fixture-password") && !status.Contains("CredentialsEncrypted") &&
        !status.Contains("providerOptionsJson", StringComparison.OrdinalIgnoreCase), "status contains no credentials");
    var configurationResponse = await http.GetAsync(configurationPath);
    using var configurationJson = JsonDocument.Parse(await configurationResponse.Content.ReadAsStringAsync());
    var returnedConfiguration = configurationJson.RootElement.GetProperty("data");
    Check(configurationResponse.IsSuccessStatusCode && configurationResponse.Headers.CacheControl?.NoStore == true &&
        returnedConfiguration.GetProperty("providerCode").GetString() == "Floosak" &&
        returnedConfiguration.GetProperty("providerOptionsJson").GetString() == expectedJson,
        "admin edit decrypts exact persisted configuration with no-store");
    using var disabledConfiguration = JsonDocument.Parse(await http.GetStringAsync($"/api/admin/payment-methods/{disabledMethodId}/wallet-login/configuration"));
    Check(disabledConfiguration.RootElement.GetProperty("data").GetProperty("providerOptionsJson").GetString() == expectedJson,
        "disabled wallet credentials remain editable");
    Check((await http.GetAsync("/api/admin/payment-methods/2147483647/wallet-login/configuration")).StatusCode == HttpStatusCode.NotFound,
        "missing payment method configuration returns not found");
    Check((await http.PostAsync($"/api/admin/payment-methods/{methodId}/wallet-login/retry", null)).IsSuccessStatusCode, "admin retry queued");

    await using var db = new YemenDriveDbContext(new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options);
    var noCredentials = new PaymentMethodEntity { Code = "fixture-unconfigured", NameAr = "empty", DescriptionAr = "fixture", ProviderCode = "Floosak", Kind = PaymentMethodKind.ExternalWallet, IsActive = true };
    var inactiveAdmin = new User { PhoneNumber = "700000093", DisplayName = "Fixture Inactive Admin", PasswordHash = "fixture", Role = UserRole.Admin, IsActive = false };
    db.AddRange(noCredentials, inactiveAdmin);
    await db.SaveChangesAsync();
    using var missingConfiguration = JsonDocument.Parse(await http.GetStringAsync($"/api/admin/payment-methods/{noCredentials.Id}/wallet-login/configuration"));
    Check(missingConfiguration.RootElement.GetProperty("data").GetProperty("providerOptionsJson").ValueKind == JsonValueKind.Null,
        "unconfigured wallet returns null instead of fabricated credentials");
    http.DefaultRequestHeaders.Remove("X-Fixture-User");
    http.DefaultRequestHeaders.Add("X-Fixture-User", inactiveAdmin.Id.ToString());
    Check((await http.GetAsync(configurationPath)).StatusCode == HttpStatusCode.Unauthorized, "inactive admin configuration rejected");
    http.DefaultRequestHeaders.Remove("X-Fixture-User");
    http.DefaultRequestHeaders.Add("X-Fixture-User", adminId.ToString());

    var account = await db.PaymentProviderAccounts.SingleAsync(x => x.PaymentMethodId == methodId);
    var ciphertext = account.CredentialsEncrypted;
    var version = account.ConfigurationVersion;
    foreach (var unreadable in new[] { "corrupt-fixture-password", protector.Protect("Floosak", "{invalid-fixture-password"), protector.Protect("Floosak", "[]"), protector.Protect("Jaib", expectedJson) })
    {
        account.CredentialsEncrypted = unreadable;
        await db.SaveChangesAsync();
        var failure = await http.GetAsync(configurationPath);
        var failureBody = await failure.Content.ReadAsStringAsync();
        Check(failure.StatusCode == HttpStatusCode.Conflict && failure.Headers.CacheControl?.NoStore == true &&
            failureBody.Contains("wallet_credentials_unreadable") && !failureBody.Contains("fixture-password") && !failureBody.Contains(unreadable),
            "unreadable persisted credentials return only a safe code with no-store");
        await db.Entry(account).ReloadAsync();
        Check(account.CredentialsEncrypted == unreadable && account.ConfigurationVersion == version,
            "failed configuration reads preserve stored credentials and revision");
    }
    account.CredentialsEncrypted = ciphertext;
    account.ProviderCode = "Jaib";
    await db.SaveChangesAsync();
    var mismatch = await http.GetAsync(configurationPath);
    Check(mismatch.StatusCode == HttpStatusCode.Conflict && (await mismatch.Content.ReadAsStringAsync()).Contains("wallet_configuration_mismatch"),
        "provider mismatch cannot return another provider credentials");
    account.ProviderCode = "Floosak";
    await db.SaveChangesAsync();
    Console.WriteLine("PASS: admin-only saved JSON retrieval, no-store, unchanged credentials, missing configuration and safe decryption failures.");
    await app.StopAsync();
}

static void Reject(Action action, string code)
{
    try { action(); throw new Exception("Expected rejection: " + code); }
    catch (ServiceException exception)
    {
        Check(exception.Code == code && !exception.Message.Contains("hidden-value"), "safe validation error " + code);
    }
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
}

sealed class CurrentUser(int id) : ICurrentUserContext { public int? UserId => id; }
sealed class HeaderCurrentUser(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor) : ICurrentUserContext
{
    public int? UserId => int.TryParse(accessor.HttpContext?.Request.Headers["X-Fixture-User"], out var id) ? id : null;
}
sealed class RecordingFactory : IWalletLoginClientFactory
{
    public List<RecordingClient> Clients { get; } = [];
    public bool NextSuccess { get; set; } = true;
    public IWalletLoginClient Create()
    {
        var client = new RecordingClient(NextSuccess);
        Clients.Add(client);
        return client;
    }
}
sealed class RecordingClient(bool success) : IWalletLoginClient
{
    public int Calls { get; private set; }
    public bool Disposed { get; private set; }
    public Task<bool> LoginAsync(WalletOptions options, CancellationToken token)
    { Calls++; return Task.FromResult(success); }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

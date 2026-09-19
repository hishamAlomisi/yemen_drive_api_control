using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using YemenDrive.Api;
using YemenDrive.Application.Users;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Maps;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Database.Providers;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using DbUser = YemenDrive.Database.Entities.User;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.WebHost.UseUrls(builder.Configuration["ApiUrl"] ?? "http://localhost:5080");

var settingsPath = Path.Combine(builder.Environment.ContentRootPath, "data", "database.settings.json");
builder.Services.AddSingleton(new DatabaseConfigurationStore(settingsPath));
builder.Services.AddSingleton<IDatabaseProvider, SqlServerDatabaseProvider>();
builder.Services.AddSingleton<DatabaseConfigurator>();
builder.Services.AddSingleton<OtpChallengeStore>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
builder.Services.AddScoped<DevelopmentDataSeeder>();
builder.Services.AddScoped<AccountingPostingService>();
builder.Services.AddScoped<RideAccountingPostingService>();
builder.Services.AddScoped<FinancialAccountProvisioningService>();
// Google provider keys are read only from server configuration (for example
// environment variables or user-secrets). They are never persisted or sent
// to Flutter clients.
builder.Services.AddSingleton(
    builder.Configuration.GetSection("GoogleMaps").Get<GoogleMapsOptions>()
    ?? new GoogleMapsOptions());
builder.Services.AddHttpClient<GoogleMapsGateway>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString);

builder.Services.AddDbContext<YemenDriveDbContext>((serviceProvider, options) =>
{
    var settings = serviceProvider.GetRequiredService<DatabaseConfigurationStore>().Current;
    if (settings is not null)
    {
        var providers = serviceProvider.GetServices<IDatabaseProvider>();
        var provider = providers.First(item =>
            string.Equals(item.Name, settings.Provider, StringComparison.OrdinalIgnoreCase));
        provider.Configure(options, settings);
    }
});

var modelServiceTypes = typeof(YemenDrive.Application.Users.User).Assembly.DefinedTypes
    .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IModelService).IsAssignableFrom(type));

foreach (var serviceType in modelServiceTypes)
{
    builder.Services.AddScoped(typeof(IModelService), serviceType);
}

builder.Services.AddScoped<ModelServiceDispatcher>();
builder.Services.AddHostedService<ConsoleSetupShortcutService>();

var app = builder.Build();

// Seed the experimental administrator whenever a configured database is available.
// This is intentionally non-destructive: it never deletes or overwrites an existing user.
try
{
    await SeedAdministratorAsync(app.Services);
}
catch (Exception exception)
{
    app.Logger.LogWarning(exception, "تعذر تهيئة حساب المدير التجريبي عند بدء التشغيل.");
}

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException)
    {
        if (context.Response.HasStarted)
        {
            throw;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(ApiResult.Fail(
            "invalid_json",
            "صيغة JSON غير صحيحة. تحقق من علامات الاقتباس والشرطة العكسية في القيم."));
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/admin"));
app.MapGet("/admin", () => Results.Redirect("/admin.html"));
app.MapGet("/setup", () => Results.Redirect("/setup.html"));

app.MapPost("/api/media/upload", async (HttpRequest request, IWebHostEnvironment environment, IFormFile file, CancellationToken token) =>
{
    if (file.Length == 0) return Results.BadRequest(new { success = false, message = "ملف الصورة فارغ." });
    var allowed = new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" };
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!allowed.Contains(extension)) return Results.BadRequest(new { success = false, message = "صيغة الصورة غير مدعومة." });
    var directory = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "uploads");
    Directory.CreateDirectory(directory);
    var name = $"{Path.GetRandomFileName().Replace('.', '_')}{extension}";
    await using (var stream = File.Create(Path.Combine(directory, name))) await file.CopyToAsync(stream, token);
    var url = $"{request.Scheme}://{request.Host}/uploads/{name}";
    return Results.Ok(new { success = true, url });
});

// Safety recordings are deliberately stored outside wwwroot. Each audio chunk
// belongs to its authenticated owner and cannot be requested as a public file.
app.MapPost("/api/safety/recordings/{recordingId:int}/chunks/{sequence:int}", async (
    int recordingId,
    int sequence,
    HttpRequest request,
    IWebHostEnvironment environment,
    YemenDriveDbContext db,
    ICurrentUserContext currentUser,
    CancellationToken token) =>
{
    const long maxChunkBytes = 256 * 1024;
    var userId = currentUser.UserId;
    if (userId is null)
        return Results.Unauthorized();
    if (sequence < 0 || request.ContentLength is <= 0 or > maxChunkBytes)
        return Results.BadRequest(ApiResult.Fail("invalid_audio_chunk", "مقطع التسجيل غير صالح."));

    var recording = await db.EmergencyRecordings
        .SingleOrDefaultAsync(x => x.Id == recordingId && x.UserId == userId, token);
    if (recording is null || recording.EndedAtUtc is not null)
        return Results.NotFound(ApiResult.Fail("safety_recording_not_available", "جلسة تسجيل السلامة غير متاحة."));

    var root = Path.Combine(environment.ContentRootPath, "data", "safety-recordings", recording.StorageKey);
    Directory.CreateDirectory(root);
    var chunkPath = Path.Combine(root, $"{sequence:D10}.pcm");
    if (File.Exists(chunkPath))
        return Results.Ok(new { success = true, alreadyReceived = true });

    await using (var stream = new FileStream(chunkPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
    {
        await request.Body.CopyToAsync(stream, token);
        if (stream.Length == 0 || stream.Length > maxChunkBytes)
        {
            stream.Close();
            File.Delete(chunkPath);
            return Results.BadRequest(ApiResult.Fail("invalid_audio_chunk", "حجم مقطع التسجيل غير مسموح."));
        }
        recording.UploadedBytes += stream.Length;
    }
    recording.LastChunkAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(token);
    return Results.Ok(new { success = true, bytesReceived = recording.UploadedBytes });
});

app.MapPost("/api/safety/recordings/{recordingId:int}/complete", async (
    int recordingId,
    SafetyRecordingCompletion request,
    IWebHostEnvironment environment,
    YemenDriveDbContext db,
    ICurrentUserContext currentUser,
    CancellationToken token) =>
{
    var userId = currentUser.UserId;
    if (userId is null) return Results.Unauthorized();
    var recording = await db.EmergencyRecordings
        .SingleOrDefaultAsync(x => x.Id == recordingId && x.UserId == userId, token);
    if (recording is null) return Results.NotFound(ApiResult.Fail("safety_recording_not_found", "تسجيل السلامة غير موجود."));
    if (recording.EndedAtUtc is null)
    {
        var root = Path.Combine(environment.ContentRootPath, "data", "safety-recordings", recording.StorageKey);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(root))
        {
            foreach (var chunk in Directory.EnumerateFiles(root, "*.pcm").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                await using var stream = File.OpenRead(chunk);
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, token)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
        }
        recording.ContentHash = Convert.ToHexString(hash.GetHashAndReset());
        recording.DurationMilliseconds = Math.Max(0, request.DurationMilliseconds);
        recording.EndedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
    }
    return Results.Ok(new { success = true, recordingId, recording.UploadedBytes, recording.DurationMilliseconds, recording.EndedAtUtc });
});

app.MapGet("/api/setup/database", (
    DatabaseConfigurationStore store,
    IEnumerable<IDatabaseProvider> providers) =>
{
    var settings = store.Current;
    return Results.Ok(new
    {
        configured = settings is not null,
        provider = settings?.Provider ?? "SqlServer",
        server = settings?.Server ?? string.Empty,
        database = settings?.Database ?? string.Empty,
        username = settings?.Username ?? string.Empty,
        integratedSecurity = settings?.IntegratedSecurity ?? false,
        trustServerCertificate = settings?.TrustServerCertificate ?? true,
        encrypt = settings?.Encrypt ?? false,
        recreateSchema = false,
        providers = providers.Select(item => new { value = item.Name, label = item.DisplayName })
    });
});

app.MapPost("/api/setup/database/test", async (
    DatabaseSettings settings,
    DatabaseConfigurator configurator,
    CancellationToken cancellationToken) =>
    Results.Ok(await configurator.TestAsync(settings, cancellationToken)));

app.MapPost("/api/setup/database/save", async (
    DatabaseSettings settings,
    DatabaseConfigurator configurator,
    CancellationToken cancellationToken) =>
    Results.Ok(await configurator.SaveAsync(settings, cancellationToken)));

app.MapPost("/api/setup/database/create", async (
    DatabaseSettings settings,
    DatabaseConfigurator configurator,
    CancellationToken cancellationToken) =>
    Results.Ok(await configurator.SaveAsync(settings, cancellationToken)));

app.MapPost("/api/admin/development/seed", async (
    IWebHostEnvironment environment,
    DevelopmentDataSeeder seeder,
    ICurrentUserContext currentUser,
    YemenDriveDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!environment.IsDevelopment())
        return Results.NotFound();
    if (!await IsAdminAsync(currentUser.UserId, db, cancellationToken))
        return Results.Ok(ApiResult.Fail("admin_authentication_required", "يلزم تسجيل الدخول بحساب الإدارة."));
    return Results.Ok(ApiResult.Ok(await seeder.SeedAsync(cancellationToken), "تمت إضافة بيانات التطوير التجريبية."));
});

app.MapPost("/api/execute", async (
    ApiRequest request,
    ModelServiceDispatcher dispatcher,
    CancellationToken cancellationToken) =>
    Results.Ok(await dispatcher.DispatchAsync(request, cancellationToken)));

app.MapPost("/api/admin/execute", async (
    ApiRequest request,
    ModelServiceDispatcher dispatcher,
    ICurrentUserContext currentUser,
    YemenDriveDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!await IsAdminAsync(currentUser.UserId, db, cancellationToken))
        return Results.Ok(ApiResult.Fail("admin_authentication_required", "يجب تسجيل الدخول بحساب الإدارة."));
    return Results.Ok(await dispatcher.DispatchAsync(request, cancellationToken));
});

app.MapAuthenticationEndpoints();

app.Run();

static async Task<bool> IsAdminAsync(int? userId, YemenDriveDbContext db, CancellationToken cancellationToken) =>
    userId is int id && await db.Users.AsNoTracking()
        .AnyAsync(x => x.Id == id && x.Role == UserRole.Admin && x.IsActive, cancellationToken);

static async Task SeedAdministratorAsync(IServiceProvider services)
{
    var configuration = services.GetRequiredService<DatabaseConfigurationStore>();
    if (configuration.Current is null) return;

    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
    await db.Database.MigrateAsync();

    const string phone = "700000001";
    var existing = await db.Users.Include(x => x.Wallet)
        .SingleOrDefaultAsync(x => x.PhoneNumber == phone);
    if (existing is not null)
    {
        var changed = false;
        if (existing.Role != UserRole.Admin) { existing.Role = UserRole.Admin; changed = true; }
        if (!existing.IsActive) { existing.IsActive = true; changed = true; }
        if (existing.Wallet is null)
        {
            existing.Wallet = new Wallet { Currency = "YER" };
            changed = true;
        }
        if (changed) await db.SaveChangesAsync();
        return;
    }

    db.Users.Add(new DbUser
    {
        PhoneNumber = phone,
        DisplayName = "YemenDrive Administrator",
        PasswordHash = PasswordHash.Create("Admin1234!"),
        Role = UserRole.Admin,
        IsActive = true,
        Wallet = new Wallet { Currency = "YER" }
    });
    await db.SaveChangesAsync();
}

public sealed record SafetyRecordingCompletion(long DurationMilliseconds);

public partial class Program;

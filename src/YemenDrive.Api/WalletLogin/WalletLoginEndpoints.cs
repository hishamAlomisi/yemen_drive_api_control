using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api.WalletLogin;

public static class WalletLoginEndpoints
{
    public static void MapWalletLoginEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/wallet-providers", async (HttpContext context,
            ICurrentUserContext user, YemenDriveDbContext db, WalletProviderCatalog catalog, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await IsAdminAsync(user, db, token)) return Results.Unauthorized();
            return Results.Ok(ApiResult.Ok(catalog.Providers));
        });

        app.MapGet("/api/admin/payment-methods/{id:int}/wallet-login", async (int id, HttpContext context,
            ICurrentUserContext user, YemenDriveDbContext db, WalletLoginRuntime runtime, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await IsAdminAsync(user, db, token)) return Results.Unauthorized();
            var method = await db.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            if (method is null) return Results.NotFound(ApiResult.Fail("payment_method_not_found", "طريقة الدفع غير موجودة."));
            var account = await db.PaymentProviderAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentMethodId == id, token);
            var status = account is null ? "NotConfigured" : !account.IsEnabled || !method.IsActive ? "Disabled" :
                runtime.GetStatus(id, account.ConfigurationVersion) ?? "Pending";
            return Results.Ok(ApiResult.Ok(new
            {
                providerCode = method.ProviderCode,
                hasCredentials = account is not null && account.CredentialsEncrypted.Length > 0,
                isEnabled = account?.IsEnabled ?? false,
                status, lastLoginAtUtc = account?.LastLoginAtUtc,
                lastAttemptAtUtc = account?.LastAttemptAtUtc,
                errorCode = status == "Failed" ? account?.LastErrorCode : null
            }));
        });

        app.MapGet("/api/admin/payment-methods/{id:int}/wallet-login/configuration", async (int id, HttpContext context,
            ICurrentUserContext user, YemenDriveDbContext db, WalletCredentialsProtector protector, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await IsAdminAsync(user, db, token)) return Results.Unauthorized();
            var method = await db.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            if (method is null) return Results.NotFound(ApiResult.Fail("payment_method_not_found", "طريقة الدفع غير موجودة."));
            var account = await db.PaymentProviderAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentMethodId == id, token);
            string? json = null;
            if (account is not null && !string.IsNullOrEmpty(account.CredentialsEncrypted))
            {
                if (method.Kind != PaymentMethodKind.ExternalWallet ||
                    !string.Equals(method.ProviderCode, account.ProviderCode, StringComparison.OrdinalIgnoreCase))
                    return Results.Conflict(ApiResult.Fail("wallet_configuration_mismatch", "إعدادات الدخول المخزنة لا تطابق مزود طريقة الدفع الحالية."));
                try
                {
                    json = protector.Unprotect(account.ProviderCode, account.CredentialsEncrypted);
                    if (Encoding.UTF8.GetByteCount(json) > 32 * 1024)
                        return UnreadableCredentials();
                    using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return UnreadableCredentials();
                }
                catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
                {
                    // Never include ciphertext, decrypted content, or exception messages in an API response.
                    return UnreadableCredentials();
                }
            }
            return Results.Ok(ApiResult.Ok(new { providerCode = method.ProviderCode, providerOptionsJson = json }));
        });

        app.MapPost("/api/admin/payment-methods/{id:int}/wallet-login/retry", async (int id, HttpContext context,
            ICurrentUserContext user, YemenDriveDbContext db, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await IsAdminAsync(user, db, token)) return Results.Unauthorized();
            var version = Guid.NewGuid();
            var changed = await db.PaymentProviderAccounts.Where(x => x.PaymentMethodId == id &&
                x.IsEnabled && x.PaymentMethod.IsActive && x.PaymentMethod.Kind == PaymentMethodKind.ExternalWallet)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.ConfigurationVersion, version)
                    .SetProperty(x => x.LoginStatus, "Pending")
                    .SetProperty(x => x.LastErrorCode, (string?)null)
                    .SetProperty(x => x.LastAttemptAtUtc, (DateTime?)null)
                    .SetProperty(x => x.LastLoginAtUtc, (DateTime?)null)
                    .SetProperty(x => x.UpdatedAtUtc, (DateTime?)DateTime.UtcNow), token);
            if (changed == 0) return Results.BadRequest(ApiResult.Fail("wallet_login_not_enabled", "فعّل طريقة الدفع وتسجيل الدخول وأدخل بيانات المحفظة أولاً."));
            return Results.Ok(ApiResult.Ok(new { status = "Pending" }, "تمت جدولة محاولة تسجيل دخول جديدة."));
        });
    }

    private static Task<bool> IsAdminAsync(ICurrentUserContext user, YemenDriveDbContext db, CancellationToken token) =>
        db.Users.AnyAsync(x => x.Id == user.UserId && x.Role == UserRole.Admin && x.IsActive, token);

    private static IResult UnreadableCredentials() => Results.Conflict(ApiResult.Fail("wallet_credentials_unreadable",
        "تعذّر قراءة إعدادات الدخول المحفوظة. تحقق من مفاتيح التشفير على الخادم؛ لم تتغير البيانات المخزنة."));
}

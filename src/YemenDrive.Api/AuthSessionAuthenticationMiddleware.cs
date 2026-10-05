using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Shared.Api;

namespace YemenDrive.Api;

public sealed class AuthSessionAuthenticationMiddleware(RequestDelegate next)
{
    public const string UserIdItem = "YemenDrive.Auth.UserId";

    public async Task InvokeAsync(
        HttpContext context,
        DatabaseConfigurationStore databaseConfiguration,
        YemenDriveDbContext db,
        AuthSessionService sessions)
    {
        var cancellationToken = context.RequestAborted;
        var path = context.Request.Path.Value;
        if (path?.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase) == true ||
            path?.Equals("/api/admin/login", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
        }

        if (IsUnauthenticatedAuthEndpoint(context.Request.Path))
        {
            await next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (databaseConfiguration.Current is null)
        {
            await WriteUnauthorizedAsync(context, cancellationToken);
            return;
        }

        var accessToken = authorization["Bearer ".Length..].Trim();
        var session = await sessions.AuthenticateAsync(db, accessToken, cancellationToken);
        if (session is null)
        {
            await WriteUnauthorizedAsync(context, cancellationToken);
            return;
        }

        context.Items[UserIdItem] = session.UserId;
        await next(context);
    }

    private static bool IsUnauthenticatedAuthEndpoint(PathString path) => path.Value is
        "/api/auth/register" or
        "/api/auth/register/request-otp" or
        "/api/auth/login" or
        "/api/auth/refresh" or
        "/api/auth/sign-in/verify-device" or
        "/api/auth/verify-otp" or
        "/api/auth/password/request-reset" or
        "/api/auth/password/reset" or
        "/api/admin/login";

    private static async Task WriteUnauthorizedAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(
            ApiResult.Fail("session_expired", "انتهت جلسة الدخول أو لم تعد صالحة. سجل الدخول من جديد."),
            cancellationToken);
    }
}

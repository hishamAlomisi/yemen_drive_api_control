using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Shared.Api;

namespace YemenDrive.Api;

public sealed class AuthSessionAuthenticationMiddleware(RequestDelegate next)
{
    public const string UserIdItem = "YemenDrive.Auth.UserId";
    public const string SessionItem = "YemenDrive.Auth.Session";

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

        var isTrackingHubPath = IsTrackingHubPath(context.Request);
        var accessToken = ReadBearerToken(context.Request.Headers.Authorization.ToString());
        if (accessToken is null && IsTrackingHubWebSocketRequest(context.Request))
            accessToken = context.Request.Query["access_token"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            if (isTrackingHubPath)
            {
                await WriteUnauthorizedAsync(context, cancellationToken);
                return;
            }
            await next(context);
            return;
        }

        if (databaseConfiguration.Current is null)
        {
            await WriteUnauthorizedAsync(context, cancellationToken);
            return;
        }

        var session = await sessions.AuthenticateAsync(db, accessToken, cancellationToken);
        if (session is null)
        {
            await WriteUnauthorizedAsync(context, cancellationToken);
            return;
        }

        context.Items[UserIdItem] = session.UserId;
        context.Items[SessionItem] = session;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString())
        ],
        authenticationType: "YemenDrive.Session"));
        context.User = principal;
        context.Features.Set<IAuthenticateResultFeature>(new AuthSessionAuthenticateResultFeature(
            AuthenticateResult.Success(new AuthenticationTicket(
                principal,
                new AuthenticationProperties
                {
                    ExpiresUtc = session.AccessExpiresAtUtc
                },
                "YemenDrive.Session"))));

        await next(context);
    }

    private static string? ReadBearerToken(string authorization)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = authorization["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static bool IsTrackingHubPath(HttpRequest request) =>
        request.Path.Equals("/hubs/tracking", StringComparison.OrdinalIgnoreCase) ||
        request.Path.Equals("/hubs/tracking/negotiate", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrackingHubWebSocketRequest(HttpRequest request) =>
        request.Path.Equals("/hubs/tracking", StringComparison.OrdinalIgnoreCase) &&
        HttpMethods.IsGet(request.Method) &&
        string.Equals(request.Query["transport"], "webSockets", StringComparison.OrdinalIgnoreCase) &&
        !request.Headers.ContainsKey("Authorization");

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

internal sealed class AuthSessionAuthenticateResultFeature(AuthenticateResult authenticateResult)
    : IAuthenticateResultFeature
{
    public AuthenticateResult? AuthenticateResult { get; set; } = authenticateResult;
}

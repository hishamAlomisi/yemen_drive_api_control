using YemenDrive.Shared.Security;

namespace YemenDrive.Api;

public sealed class HttpCurrentUserContext(IHttpContextAccessor httpContextAccessor) : ICurrentUserContext
{
    public int? UserId
    {
        get
        {
            var authorization = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(authorization) ||
                !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var token = authorization["Bearer ".Length..].Trim();
            const string developmentTokenPrefix = "demo-access-";
            return token.StartsWith(developmentTokenPrefix, StringComparison.Ordinal) &&
                   int.TryParse(token[developmentTokenPrefix.Length..], out var userId)
                ? userId
                : null;
        }
    }
}

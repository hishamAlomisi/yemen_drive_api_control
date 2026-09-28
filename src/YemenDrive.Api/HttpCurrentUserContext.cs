using YemenDrive.Shared.Security;

namespace YemenDrive.Api;

public sealed class HttpCurrentUserContext(IHttpContextAccessor httpContextAccessor) : ICurrentUserContext
{
    public int? UserId
    {
        get
        {
            var items = httpContextAccessor.HttpContext?.Items;
            return items is not null &&
                   items.TryGetValue(AuthSessionAuthenticationMiddleware.UserIdItem, out var value) &&
                   value is int userId
                ? userId
                : null;
        }
    }
}

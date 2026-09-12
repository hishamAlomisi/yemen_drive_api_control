using YemenDrive.Shared.Api;

namespace YemenDrive.Shared.Security;

public interface ICurrentUserContext
{
    int? UserId { get; }

    int RequireUserId() => UserId ?? throw new ServiceException(
        "authentication_required",
        "يجب تسجيل الدخول لتنفيذ هذه العملية.");
}

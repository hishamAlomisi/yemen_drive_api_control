using YemenDrive.Shared.Api;

namespace YemenDrive.Services.Core;

public interface IModelRequestAuthorizer
{
    Task AuthorizeAsync(ApiRequest request, CancellationToken cancellationToken);
}

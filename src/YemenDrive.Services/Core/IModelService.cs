using System.Text.Json;
using YemenDrive.Shared.Api;

namespace YemenDrive.Services.Core;

public interface IModelService
{
    string ModelName { get; }
    IReadOnlyCollection<string> Operations { get; }

    Task<ApiResult> ExecuteAsync(
        string operation,
        JsonElement data,
        CancellationToken cancellationToken);
}

using YemenDrive.Services.Core;
using YemenDrive.Shared.Configuration;
using YemenDrive.Shared.Api;

namespace YemenDrive.Services.Initialization;

public abstract class InitializationService<TModel> : ModelService<TModel> where TModel : class
{
    protected InitializationService(IDatabaseConfiguration databaseConfiguration)
        : base(databaseConfiguration)
    {
    }

    public sealed override IReadOnlyCollection<string> Operations { get; } =
        ["initialize", "prepare", "verify"];

    protected sealed override Task<object?> ExecuteOperationAsync(
        string operation,
        TModel model,
        CancellationToken cancellationToken) => operation.ToLowerInvariant() switch
    {
        "initialize" => InitializeAsync(model, cancellationToken),
        "prepare" => PrepareAsync(model, cancellationToken),
        "verify" => VerifyAsync(model, cancellationToken),
        _ => throw new ServiceException("operation_not_supported", "العملية ليست من عمليات التهيئة.")
    };

    protected virtual Task<object?> InitializeAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("التهيئة");

    protected virtual Task<object?> PrepareAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("التجهيز");

    protected virtual Task<object?> VerifyAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("التحقق");
}

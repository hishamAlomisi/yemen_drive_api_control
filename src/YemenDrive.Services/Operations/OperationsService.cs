using YemenDrive.Services.Core;
using YemenDrive.Shared.Configuration;
using YemenDrive.Shared.Api;

namespace YemenDrive.Services.Operations;

public abstract class OperationsService<TModel> : ModelService<TModel> where TModel : class
{
    protected OperationsService(IDatabaseConfiguration databaseConfiguration)
        : base(databaseConfiguration)
    {
    }

    public override IReadOnlyCollection<string> Operations { get; } =
        ["add", "create", "update", "delete", "get"];

    protected sealed override Task<object?> ExecuteOperationAsync(
        string operation,
        TModel model,
        CancellationToken cancellationToken)
    {
        switch (operation.ToLowerInvariant())
        {
            case "add" or "create":
                {
                    Vaidate(model, cancellationToken);
                    return AddAsync(model, cancellationToken);

                }
            case "update":
                {
                    return UpdateAsync(model, cancellationToken);

                }
            case "accept":
                {
                    return AcceptAsync(model, cancellationToken);

                }
            case "reject":
                {
                    return RejectAsync(model, cancellationToken);

                }
            case "cancel":
                {
                    return CancelAsync(model, cancellationToken);

                }
            case "delete":
                {
                    return DeleteAsync(model, cancellationToken);

                }
            case "get":
                {
                    return GetAsync(model, cancellationToken);

                }
            default:
                {
                    throw new ServiceException("operation_not_supported", "العملية ليست من عمليات الإضافة والتعديل والحذف.");
                }

        }
    }

    protected virtual bool Vaidate(TModel model, CancellationToken cancellationToken)
    {
        return true;
    }

    protected virtual Task<object?> AddAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("الإضافة");

    protected virtual Task<object?> UpdateAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("التعديل");

    protected virtual Task<object?> DeleteAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("الحذف");

    protected virtual Task<object?> GetAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("القراءة");

    protected virtual Task<object?> AcceptAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("القبول");

    protected virtual Task<object?> RejectAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("الرفض");

    protected virtual Task<object?> CancelAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("الإلغاء");
}

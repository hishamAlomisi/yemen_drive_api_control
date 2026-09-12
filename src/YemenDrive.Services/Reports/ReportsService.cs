using YemenDrive.Services.Core;
using YemenDrive.Shared.Configuration;
using YemenDrive.Shared.Api;

namespace YemenDrive.Services.Reports;

public abstract class ReportsService<TModel> : ModelService<TModel> where TModel : class
{
    protected ReportsService(IDatabaseConfiguration databaseConfiguration)
        : base(databaseConfiguration)
    {
    }

    public sealed override IReadOnlyCollection<string> Operations { get; } =
        ["report", "list", "search"];

    protected sealed override Task<object?> ExecuteOperationAsync(
        string operation,
        TModel model,
        CancellationToken cancellationToken) => operation.ToLowerInvariant() switch
    {
        "report" => ReportAsync(model, cancellationToken),
        "list" => ListAsync(model, cancellationToken),
        "search" => SearchAsync(model, cancellationToken),
        _ => throw new ServiceException("operation_not_supported", "العملية ليست من عمليات التقارير.")
    };

    protected virtual Task<object?> ReportAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("إنشاء التقرير");

    protected virtual Task<object?> ListAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("عرض القائمة");

    protected virtual Task<object?> SearchAsync(TModel model, CancellationToken cancellationToken) =>
        NotImplemented("البحث");
}

using YemenDrive.Shared.Api;

namespace YemenDrive.Services.Core;

public sealed class ModelServiceDispatcher(IEnumerable<IModelService> services)
{
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<IModelService>> _services = services
        .GroupBy(service => service.ModelName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => (IReadOnlyCollection<IModelService>)group.ToArray(),
            StringComparer.OrdinalIgnoreCase);

    public Task<ApiResult> DispatchAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return Task.FromResult(ApiResult.Fail("model_required", "اسم المودل مطلوب."));
        }

        if (string.IsNullOrWhiteSpace(request.Operation))
        {
            return Task.FromResult(ApiResult.Fail("operation_required", "اسم العملية مطلوب."));
        }

        if (!_services.TryGetValue(request.Model, out var modelServices))
        {
            return Task.FromResult(ApiResult.Fail("model_not_supported", $"المودل '{request.Model}' غير مسجل في API."));
        }

        var service = modelServices.FirstOrDefault(item =>
            item.Operations.Contains(request.Operation, StringComparer.OrdinalIgnoreCase));

        return service is null
            ? Task.FromResult(ApiResult.Fail("operation_not_supported", "الكلاس المسجل لا يدعم العملية المطلوبة."))
            : service.ExecuteAsync(request.Operation, request.Data, cancellationToken);
    }

}

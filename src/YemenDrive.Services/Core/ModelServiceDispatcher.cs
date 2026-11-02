using YemenDrive.Shared.Api;

namespace YemenDrive.Services.Core;

public sealed class ModelServiceDispatcher(IEnumerable<IModelService> services, IModelRequestAuthorizer? authorizer = null)
{
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<IModelService>> _services = services
        .GroupBy(service => service.ModelName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => (IReadOnlyCollection<IModelService>)group.ToArray(),
            StringComparer.OrdinalIgnoreCase);

    public async Task<ApiResult> DispatchAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return ApiResult.Fail("model_required", "اسم المودل مطلوب.");
        }

        if (string.IsNullOrWhiteSpace(request.Operation))
        {
            return ApiResult.Fail("operation_required", "اسم العملية مطلوب.");
        }
        try
        {
            if (authorizer != null) await authorizer.AuthorizeAsync(request, cancellationToken);
        }
        catch (ServiceException error) { return ApiResult.Fail(error.Code, error.Message); }

        if (!_services.TryGetValue(request.Model, out var modelServices))
        {
            return ApiResult.Fail("model_not_supported", $"المودل '{request.Model}' غير مسجل في API.");
        }

        var service = modelServices.FirstOrDefault(item =>
            item.Operations.Contains(request.Operation, StringComparer.OrdinalIgnoreCase));

        return service is null
            ? ApiResult.Fail("operation_not_supported", "الكلاس المسجل لا يدعم العملية المطلوبة.")
            : await service.ExecuteAsync(request.Operation, request.Data, cancellationToken);
    }

}

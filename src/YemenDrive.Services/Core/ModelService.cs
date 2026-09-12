using System.Text.Json;
using YemenDrive.Shared.Configuration;
using YemenDrive.Shared.Api;

namespace YemenDrive.Services.Core;

public abstract class ModelService<TModel> : IModelService where TModel : class
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IDatabaseConfiguration _databaseConfiguration;

    protected ModelService(IDatabaseConfiguration databaseConfiguration)
    {
        _databaseConfiguration = databaseConfiguration;
    }

    public virtual string ModelName => typeof(TModel).Name;
    public abstract IReadOnlyCollection<string> Operations { get; }

    public async Task<ApiResult> ExecuteAsync(
        string operation,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        try
        {
            // Older clients used {"id":"current"} for account-scoped
            // operations. The authenticated user is already available from
            // the bearer token, so remove that compatibility marker before
            // deserializing strongly typed integer ids.
            if (data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("id", out var identity) &&
                identity.ValueKind == JsonValueKind.String &&
                string.Equals(identity.GetString(), "current", StringComparison.OrdinalIgnoreCase))
            {
                var values = data.EnumerateObject()
                    .Where(item => !string.Equals(item.Name, "id", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(item => item.Name, item => item.Value);
                data = JsonSerializer.SerializeToElement(values, SerializerOptions);
            }

            var model = data.Deserialize<TModel>(SerializerOptions);
            if (model is null)
            {
                return ApiResult.Fail("invalid_request", "تعذر قراءة بيانات المودل.");
            }

            EnsureDatabaseConfigured();

            var errors = await ValidateAsync(operation, model, cancellationToken);
            if (errors.Count > 0)
            {
                return ApiResult.Invalid(errors);
            }

            await BeforeExecuteAsync(operation, model, cancellationToken);
            var result = await ExecuteOperationAsync(operation, model, cancellationToken);
            await AfterExecuteAsync(operation, model, result, cancellationToken);

            return ApiResult.Ok(result, GetSuccessMessage(operation));
        }
        catch (JsonException)
        {
            return ApiResult.Fail("invalid_request", "صيغة بيانات المودل غير صحيحة.");
        }
        catch (ServiceException exception)
        {
            return ApiResult.Fail(exception.Code, exception.Message);
        }
        catch (Exception)
        {
            return ApiResult.Fail("unexpected_error", "حدث خطأ غير متوقع أثناء تنفيذ العملية.");
        }
    }

    protected virtual string GetSuccessMessage(string operation) => "تمت العملية بنجاح.";

    protected virtual ValueTask<IReadOnlyCollection<ValidationError>> ValidateAsync(
        string operation,
        TModel model,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyCollection<ValidationError>>([]);

    protected virtual Task BeforeExecuteAsync(
        string operation,
        TModel model,
        CancellationToken cancellationToken) => Task.CompletedTask;

    protected abstract Task<object?> ExecuteOperationAsync(
        string operation,
        TModel model,
        CancellationToken cancellationToken);

    protected virtual Task AfterExecuteAsync(
        string operation,
        TModel model,
        object? result,
        CancellationToken cancellationToken) => Task.CompletedTask;

    protected static Task<object?> NotImplemented(string operation) =>
        throw new ServiceException("operation_not_implemented", $"عملية {operation} غير منفذة لهذا الكلاس.");

    protected void EnsureDatabaseConfigured()
    {
        if (!_databaseConfiguration.IsConfigured)
        {
            throw new ServiceException(
                "database_not_configured",
                "يجب إعداد اتصال قاعدة البيانات قبل تنفيذ العملية.");
        }
    }
}

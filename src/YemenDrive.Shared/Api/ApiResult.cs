namespace YemenDrive.Shared.Api;

public sealed record ApiResult(
    bool Success,
    string Code,
    string Message,
    object? Data = null,
    IReadOnlyCollection<ValidationError>? Errors = null)
{
    public static ApiResult Ok(object? data, string message = "تمت العملية بنجاح.") =>
        new(true, "success", message, data);

    public static ApiResult Fail(string code, string message) =>
        new(false, code, message);

    public static ApiResult Invalid(IReadOnlyCollection<ValidationError> errors) =>
        new(false, "validation_error", "البيانات المرسلة غير صحيحة.", Errors: errors);
}

public sealed record ValidationError(string Field, string Message);

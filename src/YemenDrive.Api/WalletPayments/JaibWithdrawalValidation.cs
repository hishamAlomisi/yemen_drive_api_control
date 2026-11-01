using System.Text.Json;
using System.Globalization;
using eqsWallets;

namespace YemenDrive.Api.WalletPayments;

/// <summary>Recognizes an explicit Jaib withdrawal rejection when its error code is unknown to the SDK.</summary>
public static class JaibWithdrawalValidation
{
    public const string PreparationPath = "/api/v1/PJCash/PCashoutInitial";
    public const string ConfirmationPath = "/api/v1/PJCash/PCashoutConfirm";
    private static readonly AsyncLocal<Attempt?> Current = new();

    private sealed class Attempt(string path, string? submittedCode)
    {
        public string Path { get; } = path;
        public string? SubmittedCode { get; } = submittedCode;
        public int Responses;
        public string? Message;
        public int? ProviderErrorCode;
    }

    public static void Observe(string path, int httpStatus, string? body, string? password = null)
    {
        var attempt = Current.Value;
        if (attempt is null || path != attempt.Path) return;
        Interlocked.Increment(ref attempt.Responses);
        var rejection = ReadRejection(httpStatus, body);
        if (rejection is null) return;
        attempt.ProviderErrorCode = rejection.Value.Code;
        var errorCode = rejection.Value.Code == 49 ? WalletErrorCode.InsufficientFunds : WalletErrorCode.Rejected;
        var sanitized = WalletProviderMessageFormatter.Format(new WalletStepResult
        { Status = OperationStatus.Failed, ErrorCode = errorCode, Message = rejection.Value.Message }, "Failed", attempt.SubmittedCode, "Jaib");
        attempt.Message = WalletProviderMessageFormatter.Format(new WalletStepResult
        { Status = OperationStatus.Failed, ErrorCode = errorCode, Message = sanitized ?? string.Empty }, "Failed", password, "Jaib");
    }

    public static async Task<WalletStepResult> RunAsync(string path, Func<Task<WalletStepResult>> request, string? submittedCode = null)
    {
        if (path is not (PreparationPath or ConfirmationPath)) throw new ArgumentException("Unsupported withdrawal stage", nameof(path));
        var previous = Current.Value;
        var attempt = new Attempt(path, submittedCode);
        Current.Value = attempt;
        try
        {
            var result = await request();
            if (attempt.Responses != 1 || attempt.Message is null || result.Status != OperationStatus.Unknown ||
                result.Success || result.Receipt?.CompletedAt is not null) return result;
            // A structured success:false/error reply rejects this call even if the SDK does not know
            // the numeric error code. No reply, conflicting result or receipt remains unchanged.
            return new WalletStepResult
            {
                Status = OperationStatus.Failed,
                ErrorCode = attempt.ProviderErrorCode == 49 ? WalletErrorCode.InsufficientFunds : WalletErrorCode.Rejected,
                Message = attempt.Message, OperationId = result.OperationId, RequestId = result.RequestId,
                Receipt = result.Receipt, ProviderReference = result.ProviderReference,
                ProviderCode = attempt.ProviderErrorCode?.ToString(CultureInfo.InvariantCulture)
            };
        }
        finally { Current.Value = previous; }
    }

    private static (int Code, string Message)? ReadRejection(int httpStatus, string? body)
    {
        if (httpStatus is not (200 or 400 or 422 or 500) || string.IsNullOrWhiteSpace(body) || body.Length > 16 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicates(root) ||
                !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.False ||
                root.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null ||
                root.TryGetProperty("unAuthorizedRequest", out var unauthorized) && unauthorized.ValueKind != JsonValueKind.False ||
                root.TryGetProperty("targetUrl", out var targetUrl) && targetUrl.ValueKind != JsonValueKind.Null ||
                !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object || HasDuplicates(error) ||
                !error.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number) || number == 0 ||
                !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(message.GetString())) return null;
            // Jaib uses HTTP 500 for business rejections. Require its full ABP failure envelope
            // for that status; a generic server error does not establish the payment outcome.
            if (httpStatus == 500 &&
                (!root.TryGetProperty("__abp", out var abp) || abp.ValueKind != JsonValueKind.True ||
                 !root.TryGetProperty("result", out result) || result.ValueKind != JsonValueKind.Null ||
                 !root.TryGetProperty("unAuthorizedRequest", out unauthorized) || unauthorized.ValueKind != JsonValueKind.False)) return null;
            return (number, message.GetString()!.Trim());
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool HasDuplicates(JsonElement value) =>
        value.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1);
}

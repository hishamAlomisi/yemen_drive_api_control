using System.Text.Json;
using eqsWallets;

namespace YemenDrive.Api.WalletPayments;

/// <summary>Recognizes structured confirmation rejections missing is_success. Retains only sanitized customer text.</summary>
public static class FloosakOtpValidation
{
    public const string MismatchMessage = "OTP number is not match with transaction id";
    private static readonly AsyncLocal<Attempt?> Current = new();

    private sealed class Attempt(string? submittedCode)
    {
        public int Responses;
        public string? Message;
        public string? SubmittedCode { get; } = submittedCode;
    }

    public static void Observe(string path, int httpStatus, string? body, string? password = null)
    {
        var attempt = Current.Value;
        if (attempt is null || path != "/api/v1/agent/offline_cashout/confirm") return;
        Interlocked.Increment(ref attempt.Responses);
        var message = RejectionMessage(httpStatus, body);
        if (message is null) return;
        var sanitized = WalletProviderMessageFormatter.Format(new WalletStepResult
        { Status = OperationStatus.Failed, ErrorCode = WalletErrorCode.InvalidRequest, Message = message }, "Failed", attempt.SubmittedCode);
        attempt.Message = WalletProviderMessageFormatter.Format(new WalletStepResult
        { Status = OperationStatus.Failed, ErrorCode = WalletErrorCode.InvalidRequest, Message = sanitized ?? string.Empty }, "Failed", password);
    }

    public static async Task<WalletStepResult> CompleteAsync(Func<Task<WalletStepResult>> complete, string? submittedCode = null)
    {
        var previous = Current.Value;
        var attempt = new Attempt(submittedCode);
        Current.Value = attempt;
        try
        {
            var result = await complete();
            if (attempt.Responses != 1 || attempt.Message is null || result.Status != OperationStatus.Unknown ||
                result.ErrorCode != WalletErrorCode.InvalidProviderResponse || result.Receipt?.CompletedAt is not null) return result;
            // HTTP 422 with structured validation errors rejects the submitted confirmation.
            // A timeout, arbitrary provider text, or a payment receipt must never take this path.
            return new WalletStepResult
            {
                Status = OperationStatus.Failed, ErrorCode = WalletErrorCode.InvalidRequest,
                Message = attempt.Message, OperationId = result.OperationId, RequestId = result.RequestId,
                Receipt = result.Receipt, ProviderReference = result.ProviderReference
            };
        }
        finally { Current.Value = previous; }
    }

    private static string? RejectionMessage(int httpStatus, string? body)
    {
        if (httpStatus != 422 || string.IsNullOrWhiteSpace(body) || body.Length > 16 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1) ||
                root.TryGetProperty("is_success", out var success) && success.ValueKind != JsonValueKind.False ||
                root.TryGetProperty("statusCode", out var statusCode) && statusCode.ValueKind != JsonValueKind.Null &&
                    (statusCode.ValueKind != JsonValueKind.Number || !statusCode.TryGetInt32(out var code) || code != httpStatus) ||
                root.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null ||
                !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(message.GetString()) ||
                !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object) return null;
            var fields = errors.EnumerateObject().ToArray();
            if (fields.Length == 0 || fields.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)) return null;
            return fields.All(field =>
                field.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.Value.GetString()) ||
                field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0 &&
                field.Value.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())))
                ? message.GetString()!.Trim() : null;
        }
        catch (JsonException) { return null; }
    }
}

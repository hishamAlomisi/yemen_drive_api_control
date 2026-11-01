using System.Text.Json;
using eqsWallets;

namespace YemenDrive.Api.WalletPayments;

/// <summary>Recognizes validation rejection before the OTP workflow. Only sanitized customer text is retained.</summary>
public static class FloosakPreparationValidation
{
    private sealed class Attempt { public int Responses; public string? Message; }
    private static readonly AsyncLocal<Attempt?> Current = new();

    public static void Observe(string path, int status, string? body, string? password)
    {
        var attempt = Current.Value;
        if (attempt is null || path != "/api/v1/agent/offline_cashout") return;
        Interlocked.Increment(ref attempt.Responses);
        if (status != 422 || string.IsNullOrWhiteSpace(body) || body.Length > 16 * 1024) return;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1) ||
                root.TryGetProperty("is_success", out var success) && success.ValueKind != JsonValueKind.False ||
                root.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null ||
                !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object || !errors.EnumerateObject().Any() ||
                !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) return;
            if (!errors.EnumerateObject().All(field =>
                field.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.Value.GetString()) ||
                field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0 &&
                field.Value.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())))) return;
            attempt.Message = WalletProviderMessageFormatter.Format(new WalletStepResult
            { Status = OperationStatus.Failed, ErrorCode = WalletErrorCode.InvalidRequest, Message = message.GetString() ?? string.Empty }, "Failed", password);
        }
        catch (JsonException) { }
    }

    public static async Task<WalletStepResult> RequestAsync(Func<Task<WalletStepResult>> request)
    {
        var previous = Current.Value;
        var attempt = new Attempt(); Current.Value = attempt;
        try
        {
            var result = await request();
            return attempt.Responses == 1 && attempt.Message is not null && result.Status == OperationStatus.Unknown &&
                result.ErrorCode == WalletErrorCode.InvalidProviderResponse && result.Receipt?.CompletedAt is null
                && result.ProviderReference is null && result.Receipt?.Reference.ProviderTransactionId is null
                ? new WalletStepResult { Status = OperationStatus.Failed, ErrorCode = WalletErrorCode.InvalidRequest,
                    Message = attempt.Message, RequestId = result.RequestId, OperationId = result.OperationId }
                : result;
        }
        finally { Current.Value = previous; }
    }
}

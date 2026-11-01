using System.Globalization;
using System.Text.Json;

namespace YemenDrive.Api.WalletPayments;

/// <summary>Describes only the fixed response contract, never provider values or arbitrary field names.</summary>
public static class WalletProviderResponseDiagnostics
{
    public static string DescribeFloosakPreparation(int httpStatus, string? body)
    {
        var fields = new Dictionary<string, object?> { ["httpStatus"] = httpStatus is >= 100 and <= 599 ? httpStatus : 0 };
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64 * 1024)
        {
            fields["response"] = string.IsNullOrWhiteSpace(body) ? "Empty" : "TooLarge";
            return JsonSerializer.Serialize(fields);
        }
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            fields["response"] = Kind(root);
            if (root.ValueKind == JsonValueKind.Object)
            {
                var success = Property(root, "is_success");
                fields["is_success"] = Kind(success);
                var code = Property(root, "statusCode");
                fields["statusCodeType"] = Kind(code);
                // Status codes are small protocol numbers. Text and large numbers are never copied.
                if ((code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) ||
                    code.ValueKind == JsonValueKind.String && int.TryParse(code.GetString(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out number)) && number is >= 0 and <= 9999)
                    fields["statusCode"] = number;
                fields["message"] = Kind(Property(root, "message"));
                var data = Property(root, "data");
                fields["data"] = Kind(data);
                foreach (var name in new[] { "id", "net", "name", "fee_currency_id", "fee_currency_amount", "currency", "status" })
                    fields["data." + name] = Kind(Property(data, name));
                var errors = Property(root, "errors");
                fields["errors"] = Kind(errors);
                foreach (var name in new[] { "source_wallet_id", "request_id", "target_phone", "amount", "fee_currency_id" })
                {
                    var error = Property(errors, name);
                    if (error.ValueKind != JsonValueKind.Undefined) fields["errors." + name] = Kind(error);
                }
            }
        }
        catch (JsonException) { fields["response"] = "InvalidJson"; }
        return JsonSerializer.Serialize(fields);
    }

    private static JsonElement Property(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;

    private static string Kind(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? "Missing" : value.ValueKind.ToString();
}

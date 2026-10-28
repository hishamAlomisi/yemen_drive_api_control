using System.Globalization;
using System.Text.Json;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

/// <summary>
/// Produces the small, verified set of notification metadata that a client may
/// use to decide which already-authorized screen to open. Notification.DataJson
/// is deliberately not forwarded as raw JSON.
/// </summary>
internal static class NotificationClientPayload
{
    private const int MaxDataJsonLength = 4_096;
    private const int MaxActionLength = 64;
    private const int MaxStatusLength = 64;

    // These are identifiers and state hints only. A client maps an action to a
    // fixed local destination; raw routes, URLs, and arbitrary notification data
    // can never be delivered through this contract.
    private static readonly HashSet<string> IdentifierKeys = new(StringComparer.Ordinal)
    {
        "rideId",
        "offerId",
        "paymentId",
        "walletTransactionId",
        "cashCollectionApprovalId",
        "cashPaymentRequestId",
        "cancellationRequestId",
        "routeChangeId",
        "waitingSessionId",
        "messageId",
        "safetyIncidentId",
        "safetyRecordingId",
        "driverSettlementPaymentId"
    };

    public static Dictionary<string, string> CreateFirebaseData(Notification notification)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["notificationId"] = notification.Id.ToString(CultureInfo.InvariantCulture),
            ["notificationType"] = ((int)notification.Type).ToString(CultureInfo.InvariantCulture)
        };
        AddRoutingData(data, notification.DataJson);
        return data;
    }

    public static Dictionary<string, object?> CreateSignalRNotification(
        int notificationId,
        NotificationType notificationType,
        DateTime createdAtUtc,
        string? dataJson)
    {
        // Keep id/type for currently released clients while adding the names
        // shared with Firebase payloads for new action routing.
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = notificationId,
            ["type"] = (int)notificationType,
            ["notificationId"] = notificationId,
            ["notificationType"] = (int)notificationType,
            ["createdAtUtc"] = createdAtUtc
        };

        foreach (var (key, value) in ReadRoutingData(dataJson))
            payload[key] = value;

        return payload;
    }

    private static void AddRoutingData(IDictionary<string, string> destination, string? dataJson)
    {
        foreach (var (key, value) in ReadRoutingData(dataJson))
            destination[key] = value;
    }

    private static IReadOnlyDictionary<string, string> ReadRoutingData(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson) || dataJson.Length > MaxDataJsonLength)
            return EmptyRoutingData.Instance;

        try
        {
            using var document = JsonDocument.Parse(dataJson, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return EmptyRoutingData.Instance;

            var routingData = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("action"))
                {
                    if (TryReadSafeToken(property.Value, MaxActionLength, out var action))
                        routingData["action"] = action;
                    continue;
                }

                if (IdentifierKeys.Contains(property.Name))
                {
                    if (TryReadPositiveIdentifier(property.Value, out var identifier))
                        routingData[property.Name] = identifier;
                    continue;
                }

                if (property.NameEquals("status"))
                {
                    if (TryReadSafeToken(property.Value, MaxStatusLength, out var status))
                        routingData["status"] = status;
                    continue;
                }

                if (property.NameEquals("approved") || property.NameEquals("settled"))
                {
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        routingData[property.Name] = property.Value.GetBoolean() ? "true" : "false";
                }
            }

            return routingData;
        }
        catch (JsonException)
        {
            // A malformed optional DataJson value cannot prevent the actual
            // notification from being delivered.
            return EmptyRoutingData.Instance;
        }
    }

    private static bool TryReadPositiveIdentifier(JsonElement value, out string identifier)
    {
        identifier = string.Empty;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numericValue) && numericValue > 0)
        {
            identifier = numericValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 20 ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedValue) ||
            parsedValue <= 0)
            return false;

        identifier = parsedValue.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryReadSafeToken(JsonElement value, int maxLength, out string token)
    {
        token = string.Empty;
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength) return false;

        foreach (var character in text)
        {
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.'))
                return false;
        }

        token = text;
        return true;
    }

    private static class EmptyRoutingData
    {
        public static readonly IReadOnlyDictionary<string, string> Instance =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

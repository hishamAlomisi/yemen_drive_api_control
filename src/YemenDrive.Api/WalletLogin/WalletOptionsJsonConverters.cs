using System.Text.Json;
using System.Text.Json.Serialization;
using eqsWallets;
using eqsWallets.Providers.Floosak;

namespace YemenDrive.Api.WalletLogin;

internal sealed class WalletCurrencyCodeJsonConverter : JsonConverter<CurrencyCode>
{
    public override CurrencyCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        CurrencyCode.FromCode(ReadString(ref reader));

    public override void Write(Utf8JsonWriter writer, CurrencyCode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override CurrencyCode ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => CurrencyCode.FromCode(ReadString(ref reader));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, CurrencyCode value,
        JsonSerializerOptions options) => writer.WritePropertyName(value.Value);

    private static string ReadString(ref Utf8JsonReader reader) =>
        reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName &&
        !string.IsNullOrWhiteSpace(reader.GetString())
            ? reader.GetString()!
            : throw new JsonException();
}

internal sealed class WalletReferenceJsonConverter : JsonConverter<WalletReference>
{
    public override WalletReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(ReadString(ref reader));

    public override void Write(Utf8JsonWriter writer, WalletReference value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override WalletReference ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => new(ReadString(ref reader));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, WalletReference value,
        JsonSerializerOptions options) => writer.WritePropertyName(value.Value);

    private static string ReadString(ref Utf8JsonReader reader) =>
        reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName &&
        !string.IsNullOrWhiteSpace(reader.GetString())
            ? reader.GetString()!
            : throw new JsonException();
}

internal sealed class FloosakIdJsonConverter : JsonConverter<FloosakId>
{
    public override FloosakId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var id)
            ? new FloosakId(id)
            : throw new JsonException();

    public override void Write(Utf8JsonWriter writer, FloosakId value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}

using System.Text.Json;

namespace YemenDrive.Shared.Api;

public sealed record ApiRequest(string Model, string Operation, JsonElement Data);

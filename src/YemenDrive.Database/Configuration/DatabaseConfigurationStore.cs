using System.Text.Json;
using System.Text.Json.Nodes;
using YemenDrive.Shared.Configuration;

namespace YemenDrive.Database.Configuration;

public sealed class DatabaseConfigurationStore : IDatabaseConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DatabaseSettings? _current;

    public DatabaseConfigurationStore(string filePath)
    {
        _filePath = filePath;
        _current = Load();
    }

    public DatabaseSettings? Current => _current;
    public bool IsConfigured => _current is not null;

    public async Task SaveAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            JsonObject document;
            try
            {
                document = File.Exists(_filePath)
                    ? JsonNode.Parse(await File.ReadAllTextAsync(_filePath, cancellationToken)) as JsonObject
                        ?? new JsonObject()
                    : new JsonObject();
            }
            catch (JsonException)
            {
                document = new JsonObject();
            }

            foreach (var key in document.Select(item => item.Key)
                         .Where(key => string.Equals(key, "Database", StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                document.Remove(key);
            }

            document["Database"] = JsonSerializer.SerializeToNode(settings, JsonOptions);
            await File.WriteAllTextAsync(_filePath, document.ToJsonString(JsonOptions), cancellationToken);
            _current = settings;
        }
        finally
        {
            _lock.Release();
        }
    }

    private DatabaseSettings? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            var root = document.RootElement;
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "Database", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    return JsonSerializer.Deserialize<DatabaseSettings>(property.Value.GetRawText(), JsonOptions);
                }
            }

            // Continue to support the original database.settings.json format
            // during development and upgrades from older deployments.
            var hasServer = root.EnumerateObject().Any(property =>
                string.Equals(property.Name, "Server", StringComparison.OrdinalIgnoreCase));
            return hasServer
                ? JsonSerializer.Deserialize<DatabaseSettings>(root.GetRawText(), JsonOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

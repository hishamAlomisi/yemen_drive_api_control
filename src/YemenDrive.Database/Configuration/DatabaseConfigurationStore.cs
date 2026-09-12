using System.Text.Json;
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

            await File.WriteAllTextAsync(
                _filePath,
                JsonSerializer.Serialize(settings, JsonOptions),
                cancellationToken);
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
            return JsonSerializer.Deserialize<DatabaseSettings>(File.ReadAllText(_filePath), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

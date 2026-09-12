using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Providers;

namespace YemenDrive.Database;

public sealed class DesignTimeYemenDriveDbContextFactory : IDesignTimeDbContextFactory<YemenDriveDbContext>
{
    public YemenDriveDbContext CreateDbContext(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            Path.Combine(root, "src", "YemenDrive.Api", "data", "database.settings.json"),
            Path.Combine(root, "..", "YemenDrive.Api", "data", "database.settings.json"),
            Path.Combine(root, "data", "database.settings.json")
        };
        var settingsPath = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Database settings are not configured.");
        var store = new DatabaseConfigurationStore(settingsPath);
        var settings = store.Current ?? throw new InvalidOperationException("Database settings are not configured.");
        var options = new DbContextOptionsBuilder<YemenDriveDbContext>();
        new SqlServerDatabaseProvider().Configure(options, settings);
        return new YemenDriveDbContext(options.Options);
    }
}

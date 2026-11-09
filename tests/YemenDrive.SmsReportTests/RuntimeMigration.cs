using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Communication;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Providers;

internal static class RuntimeMigration
{
    private const string Migration = "20261108182446_SmsMessageStatistics";
    internal static async Task RunAsync(bool apply)
    {
        var configuration = new DatabaseConfigurationStore(@"E:\Driver\output\api-recovery-release-20261104\api\security\settings.json");
        var settings = configuration.Current ?? throw new InvalidOperationException("Runtime database is unconfigured");
        var builder = new DbContextOptionsBuilder<YemenDriveDbContext>(); new SqlServerDatabaseProvider().Configure(builder, settings);
        await using var db = new YemenDriveDbContext(builder.Options);
        if (db.Database.HasPendingModelChanges()) throw new InvalidOperationException("Model snapshot differs");
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        if (pending.Any(x => x != Migration)) throw new InvalidOperationException("Unexpected pending migration");
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        await using (var verify = connection.CreateCommand())
        {
            verify.CommandText = "SELECT DB_NAME()";
            if (!string.Equals(Convert.ToString(await verify.ExecuteScalarAsync()), settings.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Runtime database target mismatch");
        }
        var work = @"E:\Driver\work\sms-report-admin-20261108"; Directory.CreateDirectory(work);
        string? backupPath = null;
        if (apply && pending.Length == 1)
        {
            await using var directory = connection.CreateCommand();
            directory.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
            var backupDirectory = Convert.ToString(await directory.ExecuteScalarAsync());
            if (string.IsNullOrWhiteSpace(backupDirectory)) throw new InvalidOperationException("No SQL backup path available");
            backupPath = Path.Combine(backupDirectory, "YemenDrive-before-sms-report-" + Guid.NewGuid().ToString("N") + ".bak");
            var databaseName = settings.Database.Replace("]", "]]"); var sqlPath = backupPath.Replace("'", "''");
            await using var backup = connection.CreateCommand(); backup.CommandTimeout = 300;
            backup.CommandText = $"BACKUP DATABASE [{databaseName}] TO DISK=N'{sqlPath}' WITH COPY_ONLY,CHECKSUM; RESTORE VERIFYONLY FROM DISK=N'{sqlPath}' WITH CHECKSUM;";
            await backup.ExecuteNonQueryAsync();
            await File.WriteAllTextAsync(Path.Combine(work, "database-backup-path.txt"), backupPath);
            await db.Database.MigrateAsync();
        }
        var remaining = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        SmsStatistics? statistics = remaining.Length == 0 ? await new SmsStatisticsService(db).GetAsync(default) : null;
        if (apply && remaining.Length != 0) throw new InvalidOperationException("Migration did not complete");
        var result = new { targetVerified = true, apply, pendingBefore = pending, remaining, backupVerified = backupPath is not null, statistics };
        await File.WriteAllTextAsync(Path.Combine(work, apply ? "runtime-migration.json" : "runtime-preflight.json"), JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}

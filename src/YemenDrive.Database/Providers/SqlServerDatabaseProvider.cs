using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database.Configuration;

namespace YemenDrive.Database.Providers;

public sealed class SqlServerDatabaseProvider : IDatabaseProvider
{
    public string Name => "SqlServer";
    public string DisplayName => "Microsoft SQL Server";

    public void Configure(DbContextOptionsBuilder options, DatabaseSettings settings)
    {
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server,
            InitialCatalog = settings.Database,
            IntegratedSecurity = settings.IntegratedSecurity,
            TrustServerCertificate = settings.TrustServerCertificate,
            Encrypt = settings.Encrypt,
            ConnectTimeout = 5,
            UserID = settings.IntegratedSecurity ? string.Empty : settings.Username,
            Password = settings.IntegratedSecurity ? string.Empty : settings.Password
        }.ConnectionString;

        options.UseSqlServer(connectionString);
    }
}

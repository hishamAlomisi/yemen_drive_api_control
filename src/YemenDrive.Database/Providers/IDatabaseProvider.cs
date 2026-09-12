using Microsoft.EntityFrameworkCore;
using YemenDrive.Database.Configuration;

namespace YemenDrive.Database.Providers;

public interface IDatabaseProvider
{
    string Name { get; }
    string DisplayName { get; }

    void Configure(
        DbContextOptionsBuilder options,
        DatabaseSettings settings);
}

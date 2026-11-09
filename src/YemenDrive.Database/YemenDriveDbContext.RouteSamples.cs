using Microsoft.EntityFrameworkCore;
using YemenDrive.Database.Entities;

namespace YemenDrive.Database;

public sealed partial class YemenDriveDbContext
{
    public DbSet<DriverRouteSample> DriverRouteSamples => Set<DriverRouteSample>();
    public DbSet<DriverRouteSession> DriverRouteSessions => Set<DriverRouteSession>();

    private static void ConfigureRouteSamples(ModelBuilder modelBuilder)
    {
        var samples = modelBuilder.Entity<DriverRouteSample>();
        samples.HasIndex(x => new { x.DriverId, x.SampleId }).IsUnique();
        samples.HasIndex(x => new { x.DriverId, x.SessionId, x.Sequence }).IsUnique();
        samples.HasIndex(x => new { x.RideId, x.ObservedAtUtc });
        samples.HasOne<User>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        samples.HasOne<Ride>().WithMany().HasForeignKey(x => x.RideId).OnDelete(DeleteBehavior.Restrict);
        var sessions = modelBuilder.Entity<DriverRouteSession>();
        sessions.HasIndex(x => new { x.DriverId, x.SessionId }).IsUnique();
        sessions.HasIndex(x => x.RideId);
        sessions.HasOne<User>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        sessions.HasOne<Ride>().WithMany().HasForeignKey(x => x.RideId).OnDelete(DeleteBehavior.Restrict);
    }
}

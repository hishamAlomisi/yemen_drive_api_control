using Microsoft.EntityFrameworkCore;
using YemenDrive.Database.Entities;

namespace YemenDrive.Database;

public sealed partial class YemenDriveDbContext
{
    public DbSet<RideMovementSnapshot> RideMovementSnapshots => Set<RideMovementSnapshot>();
    public DbSet<RideRequestDriverLocation> RideRequestDriverLocations => Set<RideRequestDriverLocation>();

    private static void ConfigureMovementHistory(ModelBuilder builder)
    {
        var snapshots = builder.Entity<RideMovementSnapshot>();
        snapshots.HasKey(x => x.RideId);
        snapshots.HasOne(x => x.Ride).WithOne().HasForeignKey<RideMovementSnapshot>(x => x.RideId).OnDelete(DeleteBehavior.Cascade);
        snapshots.Property(x => x.PickupLabel).HasMaxLength(1000);
        snapshots.Property(x => x.DestinationLabel).HasMaxLength(1000);
        var drivers = builder.Entity<RideRequestDriverLocation>();
        drivers.HasKey(x => new { x.RideId, x.DriverId });
        drivers.HasOne(x => x.Ride).WithMany().HasForeignKey(x => x.RideId).OnDelete(DeleteBehavior.Cascade);
        drivers.HasOne<User>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<LocationUpdate>().Property(x => x.Source).HasMaxLength(40);
        builder.Entity<Ride>().HasIndex(x => new { x.DriverId, x.CreatedAtUtc });
    }
}

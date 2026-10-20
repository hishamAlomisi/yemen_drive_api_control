namespace YemenDrive.Database.Entities;

public sealed class RideSearchView
{
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public int DriverId { get; set; }
    public User Driver { get; set; } = null!;
    public DateTime ViewedAtUtc { get; set; }
}

namespace YemenDrive.Database.Entities;

/// <summary>Request facts retained independently of later destination changes and live GPS.</summary>
public sealed class RideMovementSnapshot
{
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public double PickupLatitude { get; set; }
    public double PickupLongitude { get; set; }
    public string PickupLabel { get; set; } = string.Empty;
    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public string DestinationLabel { get; set; } = string.Empty;
    public string? PlannedRoutePolyline { get; set; }
    public double? PlannedDurationSeconds { get; set; }
    public double? PassengerLatitude { get; set; }
    public double? PassengerLongitude { get; set; }
    public double? PassengerAccuracy { get; set; }
    public DateTime? PassengerObservedAtUtc { get; set; }
}

/// <summary>The first stored position when this driver was sent this request.</summary>
public sealed class RideRequestDriverLocation
{
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public int DriverId { get; set; }
    public DateTime SentAtUtc { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTime ObservedAtUtc { get; set; }
}

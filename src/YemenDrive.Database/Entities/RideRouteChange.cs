namespace YemenDrive.Database.Entities;

public sealed class RideRouteChange : Entity
{
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public Guid RequestKey { get; set; }
    public int RequestedByUserId { get; set; }
    public int? ApprovedByUserId { get; set; }
    public string Status { get; set; } = "quoted";
    public bool IsOpen { get; set; } = true;
    public int RouteVersion { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime ChangeObservedAtUtc { get; set; }
    public double ChangeLatitude { get; set; }
    public double ChangeLongitude { get; set; }
    public string OldDestinationLabel { get; set; } = "";
    public double OldDestinationLatitude { get; set; }
    public double OldDestinationLongitude { get; set; }
    public string NewDestinationLabel { get; set; } = "";
    public double NewDestinationLatitude { get; set; }
    public double NewDestinationLongitude { get; set; }
    public string NewRoutePolyline { get; set; } = "";
    public decimal PreviousTotal { get; set; }
    public double CompletedDistanceMeters { get; set; }
    public double CompletedDurationSeconds { get; set; }
    public decimal CompletedFare { get; set; }
    public decimal OriginalServiceFee { get; set; }
    public decimal RouteChangeFee { get; set; }
    public decimal PreviousRouteChangeFees { get; set; }
    public double NewDistanceMeters { get; set; }
    public double NewDurationSeconds { get; set; }
    public decimal NewRouteFare { get; set; }
    public decimal WaitingFee { get; set; }
    public decimal NewTotal { get; set; }
}

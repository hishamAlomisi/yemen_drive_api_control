namespace YemenDrive.Database.Entities;

public sealed class RideWaitingSession : Entity
{
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public int RequestedByUserId { get; set; }
    public int? ApprovedByUserId { get; set; }
    public Guid RequestKey { get; set; }
    // One open request or active stop per ride; enforced by a filtered unique index.
    public bool IsOpen { get; set; } = true;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }
    public int FreeMinutes { get; set; }
    public decimal PricePerMinute { get; set; }
    public decimal MaxFee { get; set; }
    public decimal Fee { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTime? MotionStartedAtUtc { get; set; }
    public DateTime? LastMotionObservedAtUtc { get; set; }
    public bool GraceWarningSent { get; set; }
    public bool BillingNoticeSent { get; set; }
}

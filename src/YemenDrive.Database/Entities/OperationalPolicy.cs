namespace YemenDrive.Database.Entities;

public sealed class OperationalPolicy : Entity
{
    public double? ArrivalToleranceMeters { get; set; }
    public int? PickupGraceMinutes { get; set; }
    public int? DecisionResponseMinutes { get; set; }
    public string ApprovalBasis { get; set; } = string.Empty;
    public int? ApprovedByUserId { get; set; }
}
public sealed class PickupContactAttempt : Entity
{
    public int RideId { get; set; }
    public int ActorUserId { get; set; }
    public string Outcome { get; set; } = string.Empty;
}

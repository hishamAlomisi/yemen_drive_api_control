namespace YemenDrive.Database.Entities;

/// <summary>A financial follow-up never reserves a driver or reopens transport.</summary>
public sealed class RideFinancialCase : Entity
{
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public int CustomerId { get; set; }
    public int? DriverId { get; set; }
    public string Kind { get; set; } = "Receivable";
    public string Status { get; set; } = "Open";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "YER";
    public string SourceReference { get; set; } = string.Empty;
    public int? AssignedAdminId { get; set; }
    public string? Note { get; set; }
    public string? EvidenceReference { get; set; }
    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public decimal AccruedReceivable { get; set; }
    public decimal AccruedServiceFee { get; set; }
    public decimal AccruedDriverEntitlement { get; set; }
    public int JournalRevision { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class OperationalDecisionAudit : Entity
{
    public int ActorUserId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public int SubjectId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? ResultJson { get; set; }
}

public sealed class AccountDeletionRequest : Entity
{
    public int UserId { get; set; }
    public string Status { get; set; } = "Pending";
    public string Reason { get; set; } = string.Empty;
    public string? AdminNote { get; set; }
    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}

public sealed class AdministratorPermission : Entity
{
    public int UserId { get; set; }
    public string Permission { get; set; } = string.Empty;
}

public sealed class SupportTicketEntry : Entity
{
    public int SupportTicketId { get; set; }
    public int ActorUserId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? EvidenceReference { get; set; }
    public string Status { get; set; } = "Open";
}

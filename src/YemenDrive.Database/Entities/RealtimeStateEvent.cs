namespace YemenDrive.Database.Entities;

/// <summary>Allocated under a database transaction lock, independently for each resource.</summary>
public sealed class RealtimeStateStream
{
    public string StreamKey { get; set; } = "";
    public long Version { get; set; }
}

/// <summary>Durable invalidation, not a second copy of financial or personal data.</summary>
public sealed class RealtimeStateEvent
{
    public Guid Id { get; set; }
    // Empty is reserved for retained events produced before transaction correlation.
    public Guid OperationId { get; set; }
    public string StreamKey { get; set; } = "";
    public long Version { get; set; }
    public string Resource { get; set; } = "";
    public int ResourceId { get; set; }
    public string Changes { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public int Attempts { get; set; }
    public ICollection<RealtimeStateRecipient> Recipients { get; set; } = [];
}

public sealed class RealtimeStateRecipient
{
    // 0: existing authorized operations audience; -1: authenticated catalog invalidation.
    // Positive values always originate from database ownership, never from a client request.
    public Guid EventId { get; set; }
    public RealtimeStateEvent Event { get; set; } = null!;
    public int UserId { get; set; }
}

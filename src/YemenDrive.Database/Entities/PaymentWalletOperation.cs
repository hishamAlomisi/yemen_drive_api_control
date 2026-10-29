namespace YemenDrive.Database.Entities;

/// <summary>A durable reservation and provider outcome. Never contains credentials, OTPs or raw provider replies.</summary>
public sealed class PaymentWalletOperation : Entity
{
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public int CustomerId { get; set; }
    public int PaymentMethodId { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = null!;
    public string ProviderCode { get; set; } = "Floosak";
    public string RequestId { get; set; } = string.Empty;
    public string? ProviderOperationId { get; set; }
    public Guid RuntimeInstanceId { get; set; }
    public Guid ConfigurationVersion { get; set; }
    public string Status { get; set; } = "Processing";
    public string NextAction { get; set; } = "None";
    public string Stage { get; set; } = "Request";
    public bool IsOpen { get; set; } = true;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "YER";
    public decimal? Fees { get; set; }
    public bool FeesKnown { get; set; }
    public string FeeCurrency { get; set; } = "YER";
    public string? CustomerName { get; set; }
    public string Mobile { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }
    public string? ErrorCode { get; set; }
    public string? ProviderMessageAr { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? ProviderSucceededAtUtc { get; set; }
    public int? PaymentTransactionId { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

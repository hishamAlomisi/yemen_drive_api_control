namespace YemenDrive.Database.Entities;

/// <summary>Server-only login configuration and status for an external payment method.</summary>
public sealed class PaymentProviderAccount : Entity
{
    public int PaymentMethodId { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = null!;
    public string ProviderCode { get; set; } = string.Empty;
    public string CredentialsEncrypted { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public Guid ConfigurationVersion { get; set; } = Guid.NewGuid();
    public string LoginStatus { get; set; } = "NotConfigured";
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
}

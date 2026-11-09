namespace YemenDrive.Database.Entities;

public enum SmsDispatchState { Pending = 0, Sending = 1, Sent = 2, Rejected = 3, Unconfirmed = 4, Expired = 5 }

public sealed class SmsDispatch
{
    public long Id { get; set; }
    public SmsMessagePurpose Purpose { get; set; }
    public SmsDispatchState State { get; set; }
    public string? ProtectedPayload { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? ErrorCode { get; set; }
    public string? RequestKey { get; set; }
    public string? RequestHash { get; set; }
    public int? WalletTransactionId { get; set; }
    public WalletTransaction? WalletTransaction { get; set; }
}

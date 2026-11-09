namespace YemenDrive.Database.Entities;

public enum SmsMessagePurpose { SignIn = 1, PasswordReset = 2, Registration = 3, WalletMovement = 4, OtherOtp = 5, Manual = 6 }

// Counts provider-accepted SMS only; no phone number, message body, or OTP is stored.
public sealed class SmsMessageLog
{
    public int Id { get; set; }
    public required string MessageKey { get; set; }
    public SmsMessagePurpose Purpose { get; set; }
    public DateTime SentAtUtc { get; set; }
    public int RecipientCount { get; set; } = 1;
}

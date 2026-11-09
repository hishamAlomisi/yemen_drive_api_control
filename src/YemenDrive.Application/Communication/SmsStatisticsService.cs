using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Communication;

public sealed record SmsStatistics(long SignInCount, long PasswordResetCount, long RegistrationCount,
    long TotalCount, DateTime? LastSentAtUtc, long WalletMovementCount = 0, long OtherOtpCount = 0, long ManualCount = 0);

public sealed class SmsStatisticsService(YemenDriveDbContext db)
{
    public async Task<SmsStatistics> GetAsync(CancellationToken token)
    {
        var groups = await db.SmsMessageLogs.AsNoTracking().GroupBy(x => x.Purpose)
            .Select(group => new { Purpose = group.Key, Count = group.Sum(x => (long)x.RecipientCount), LastSentAtUtc = group.Max(x => x.SentAtUtc) })
            .ToListAsync(token);
        long Count(SmsMessagePurpose purpose) => groups.FirstOrDefault(x => x.Purpose == purpose)?.Count ?? 0;
        return new(Count(SmsMessagePurpose.SignIn), Count(SmsMessagePurpose.PasswordReset),
            Count(SmsMessagePurpose.Registration), groups.Sum(x => x.Count),
            groups.Count == 0 ? null : DateTime.SpecifyKind(groups.Max(x => x.LastSentAtUtc), DateTimeKind.Utc),
            Count(SmsMessagePurpose.WalletMovement), Count(SmsMessagePurpose.OtherOtp), Count(SmsMessagePurpose.Manual));
    }

    // Call after a real SMS provider accepts the message. FCM and console OTP never call this method.
    public async Task<bool> RecordSentAsync(string messageKey, SmsMessagePurpose purpose, DateTime sentAtUtc, CancellationToken token, int recipientCount = 1)
    {
        if (string.IsNullOrWhiteSpace(messageKey) || messageKey.Length > 128 || !Enum.IsDefined(purpose) || sentAtUtc.Kind != DateTimeKind.Utc || recipientCount is < 1 or > 100)
            throw new ServiceException("invalid_sms_record", "بيانات تسجيل الرسالة النصية غير صالحة.");
        var key = messageKey.Trim();
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [SmsMessageLogs] ([MessageKey], [Purpose], [SentAtUtc], [RecipientCount])
            SELECT {key}, {(int)purpose}, {sentAtUtc}, {recipientCount}
            WHERE NOT EXISTS (SELECT 1 FROM [SmsMessageLogs] WITH (UPDLOCK, HOLDLOCK) WHERE [MessageKey] = {key})
            """, token);
        if (changed == 0 && await db.SmsMessageLogs.AnyAsync(x => x.MessageKey == key && (x.Purpose != purpose || x.RecipientCount != recipientCount), token))
            throw new ServiceException("sms_record_conflict", "سبق تسجيل الرسالة لغرض مختلف.");
        return changed > 0;
    }
}

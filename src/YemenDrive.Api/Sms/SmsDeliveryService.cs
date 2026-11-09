using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Communication;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Api.Sms;

public sealed record SmsPayload(string Number, string Text, string[]? Numbers = null);
public sealed record ManualSmsInput(string[]? Numbers, string? Text, string? RequestKey);
public sealed record ManualSmsResult(long MessageId, string State, int RecipientCount, bool Accepted, bool Uncertain, string Message);
public sealed class SmsPayloadProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("YemenDrive.SmsPayload.v1");
    public string Protect(string number, string text) => protector.Protect(JsonSerializer.Serialize(new SmsPayload(number, text)));
    public string Protect(string[] numbers, string text) => protector.Protect(JsonSerializer.Serialize(new SmsPayload("", text, numbers)));
    public SmsPayload Unprotect(string value) => JsonSerializer.Deserialize<SmsPayload>(protector.Unprotect(value)) ?? throw new JsonException();

    public static string? NormalizeNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        var digits = new System.Text.StringBuilder();
        foreach (var character in number.Trim())
        {
            if (char.IsDigit(character)) digits.Append((int)char.GetNumericValue(character));
            else if (character is not ('+' or '-' or '(' or ')') && !char.IsWhiteSpace(character)) return null;
        }
        var normalized = digits.ToString();
        if (normalized.StartsWith("00967") && normalized.Length == 14) normalized = normalized[5..];
        else if (normalized.StartsWith("967") && normalized.Length == 12) normalized = normalized[3..];
        return normalized.Length == 9 && normalized[0] == '7' ? normalized : null;
    }
}

public sealed class SmsDeliveryService(IServiceScopeFactory scopes, SmsProviderRuntime runtime, SmsPayloadProtector protector)
{
    public Task<bool> IsConfiguredAsync(CancellationToken token) => runtime.IsConfiguredAsync(token);

    public async Task<ManualSmsResult> SendManualAsync(int administratorId, ManualSmsInput input, CancellationToken token)
    {
        if (input.Numbers is null || input.Numbers.Length is < 1 or > 100)
            throw new ServiceException("invalid_sms_recipients", "أدخل من رقم واحد إلى 100 رقم في الطلب.");
        if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > 1600)
            throw new ServiceException("invalid_sms_text", "أدخل نص الرسالة بما لا يتجاوز 1600 حرف.");
        if (!Guid.TryParse(input.RequestKey, out var requestId) || requestId == Guid.Empty)
            throw new ServiceException("invalid_sms_request", "معرف طلب الإرسال غير صالح. أعد فتح شاشة الإرسال.");
        var normalized = input.Numbers.Select(SmsPayloadProtector.NormalizeNumber).ToArray();
        if (normalized.Any(x => x is null))
            throw new ServiceException("invalid_sms_number", "يوجد رقم غير صالح. أدخل أرقام جوال يمنية من تسعة أرقام، مع أو بدون مفتاح 967.");
        var numbers = normalized.Select(x => x!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var requestKey = $"manual:{administratorId}:{requestId:N}";
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { numbers, text = input.Text }))));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var existing = await db.SmsDispatches.AsNoTracking().SingleOrDefaultAsync(x => x.RequestKey == requestKey, token);
        if (existing is null)
        {
            var connection = await runtime.ConnectAsync(token);
            if (!connection.Connected) throw new ServiceException("sms_provider_not_ready", connection.Message);
            existing = new SmsDispatch { Purpose = SmsMessagePurpose.Manual, RequestKey = requestKey, RequestHash = hash,
                CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5), ProtectedPayload = protector.Protect(numbers, input.Text) };
            db.SmsDispatches.Add(existing);
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                existing = await db.SmsDispatches.AsNoTracking().SingleOrDefaultAsync(x => x.RequestKey == requestKey, token);
                if (existing is null) throw;
            }
        }
        if (existing.RequestHash != hash) throw new ServiceException("sms_request_conflict", "استُخدم معرف الطلب لرسالة مختلفة. ابدأ رسالة جديدة.");
        if (existing.State == SmsDispatchState.Pending) await DeliverAsync(db, existing.Id, token);
        var outcome = await db.SmsDispatches.AsNoTracking().SingleAsync(x => x.Id == existing.Id, token);
        var accepted = outcome.State == SmsDispatchState.Sent;
        var uncertain = outcome.State is SmsDispatchState.Sending or SmsDispatchState.Unconfirmed;
        var message = outcome.State switch
        {
            SmsDispatchState.Sent => $"قبل المزود إرسال الرسالة إلى {numbers.Length} رقمًا.",
            SmsDispatchState.Sending => "طلب الإرسال قيد المعالجة. تحقق من النتيجة قبل إرسال طلب جديد.",
            SmsDispatchState.Unconfirmed => "نتيجة الإرسال غير مؤكدة. لم تتم إعادة إرسال الرسالة لتجنب التكرار.",
            SmsDispatchState.Expired => "انتهت صلاحية طلب الإرسال.",
            _ => "لم يقبل المزود إرسال الرسالة. تحقق من الاتصال ورصيد المزود."
        };
        return new(outcome.Id, outcome.State.ToString().ToLowerInvariant(), numbers.Length, accepted, uncertain, message);
    }

    public async Task<SmsSendResult> SendOtpAsync(string destination, string purpose, string code, DateTime expiresAtUtc, CancellationToken token, string? smsAppHash = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var phone = destination;
        if (destination.Contains('@')) phone = await db.Users.AsNoTracking().Where(x => x.IsActive && x.Email == destination.Trim().ToLowerInvariant())
            .Select(x => x.PhoneNumber).SingleOrDefaultAsync(token) ?? "";
        var number = SmsPayloadProtector.NormalizeNumber(phone);
        if (number is null) return new(false, "invalid_sms_number");
        var category = purpose.StartsWith("signIn", StringComparison.OrdinalIgnoreCase) ? SmsMessagePurpose.SignIn
            : purpose == "passwordReset" ? SmsMessagePurpose.PasswordReset
            : purpose == "signUp" ? SmsMessagePurpose.Registration : SmsMessagePurpose.OtherOtp;
        var label = category switch { SmsMessagePurpose.SignIn => "تسجيل الدخول", SmsMessagePurpose.PasswordReset => "تغيير كلمة المرور", SmsMessagePurpose.Registration => "إنشاء حساب", _ => "التحقق" };
        var message = new SmsDispatch { Purpose = category, CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = expiresAtUtc,
            ProtectedPayload = protector.Protect(number, OtpSmsFormatter.Format(label, code, smsAppHash)) };
        db.SmsDispatches.Add(message);
        await db.SaveChangesAsync(token);
        return await DeliverAsync(db, message.Id, token);
    }

    public async Task<SmsSendResult> DeliverAsync(YemenDriveDbContext db, long id, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var claimed = await db.SmsDispatches.Where(x => x.Id == id && x.State == SmsDispatchState.Pending && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, SmsDispatchState.Sending).SetProperty(x => x.ClaimedAtUtc, now), token);
        if (claimed != 1) return new(false, "sms_already_processed");
        var message = await db.SmsDispatches.AsNoTracking().SingleAsync(x => x.Id == id, token);
        SmsSendResult result;
        var recipientCount = 1;
        try
        {
            var payload = protector.Unprotect(message.ProtectedPayload!);
            var numbers = payload.Numbers ?? new[] { payload.Number };
            recipientCount = numbers.Length;
            result = await runtime.SendAsync(id, numbers, payload.Text, token);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        { result = new(false, "sms_payload_unreadable"); }
        // Persist the outcome even if the caller disconnected after sending.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var transaction = await db.Database.BeginTransactionAsync(completion.Token);
        if (result.Accepted)
            await new SmsStatisticsService(db).RecordSentAsync("dispatch:" + id, message.Purpose, DateTime.UtcNow, completion.Token, recipientCount);
        var state = result.Accepted ? SmsDispatchState.Sent : result.Uncertain ? SmsDispatchState.Unconfirmed : SmsDispatchState.Rejected;
        await db.SmsDispatches.Where(x => x.Id == id && x.State == SmsDispatchState.Sending).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.State, state).SetProperty(x => x.ProtectedPayload, (string?)null)
            .SetProperty(x => x.SentAtUtc, result.Accepted ? (DateTime?)DateTime.UtcNow : null)
            .SetProperty(x => x.ErrorCode, result.Accepted ? null : result.ErrorCode), completion.Token);
        await transaction.CommitAsync(completion.Token);
        return result;
    }
}

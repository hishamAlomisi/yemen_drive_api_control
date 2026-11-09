using Microsoft.Extensions.Configuration;
using YemenDrive.Api.Sms;

namespace YemenDrive.Api;

// Ephemeral transport input. Neither this value nor its code is persisted or
// returned by an authentication endpoint.
public sealed record OtpTestPushMessage(string FcmToken, string ChallengeId, string Purpose,
    string ClientType, DateTime ExpiresAtUtc, string Code, string? NotificationRenderer = null);

public interface IOtpTestPushSender
{
    Task<string> SendTestOtpAsync(OtpTestPushMessage message, CancellationToken cancellationToken);
}

public sealed record OtpIssueResult(string? ChallengeId, int ExpiresInSeconds,
    string? ErrorCode, string Message)
{
    public bool Success => ErrorCode is null && ChallengeId is not null;
}

public sealed class OtpDeliveryService(OtpChallengeStore store, IOtpTestPushSender sender,
    IConfiguration configuration, SmsDeliveryService? sms = null)
{
    public bool TestPushEnabled => configuration.GetValue<bool>("Otp:EnableTestPushDelivery");
    public async Task<bool> UsesSmsAsync(CancellationToken token) => sms is not null && await sms.IsConfiguredAsync(token);

    public async Task<OtpIssueResult> CreateAsync(string destination, string purpose,
        string? otpPushToken, string? deviceId, string? clientType, CancellationToken token, string? smsAppHash = null,
        string? notificationRenderer = null)
    {
        if (await UsesSmsAsync(token))
        {
            var smsChallenge = store.CreateForDelivery(destination, purpose);
            try
            {
                var sent = await sms!.SendOtpAsync(destination, purpose, smsChallenge.Code, smsChallenge.ExpiresAtUtc, token, smsAppHash);
                if (sent.Accepted && !token.IsCancellationRequested)
                    return new(smsChallenge.ChallengeId, 300, null, "تم إرسال رمز التحقق برسالة نصية.");
                store.CancelChallenge(smsChallenge.ChallengeId);
                return Fail(sent.ErrorCode, sent.ErrorCode == "invalid_sms_number" ? "رقم الهاتف غير صالح لاستقبال الرسائل النصية."
                    : "تعذر إرسال رمز التحقق. تحقق من اتصال مزود الرسائل وتفعيل حسابه.");
            }
            catch
            {
                store.CancelChallenge(smsChallenge.ChallengeId);
                token.ThrowIfCancellationRequested();
                return Fail("sms_delivery_failed", "تعذر إرسال رمز التحقق برسالة نصية. حاول مجددًا.");
            }
        }
        if (!TestPushEnabled)
        {
            var local = store.TryCreate(destination, purpose);
            return local is null
                ? Fail("otp_delivery_unavailable", "OTP غير متاح في هذه البيئة لعدم تهيئة مزود الرسائل.")
                : new(local.Value.ChallengeId, local.Value.ExpiresInSeconds, null,
                    "تم إنشاء رمز التحقق. راجع كونسول التطوير المحلي.");
        }
        var client = clientType?.Trim().ToLowerInvariant();
        if (client is not ("customer" or "driver"))
            return Fail("invalid_client_type", "نوع التطبيق غير صالح لإشعار رمز التحقق التجريبي.");
        if (!TrustedDeviceCredentialService.IsValidDeviceId(deviceId))
            return Fail("invalid_device", "معرّف الجهاز مطلوب أو غير صالح.");
        var pushToken = otpPushToken?.Trim();
        if (string.IsNullOrWhiteSpace(pushToken))
            return Fail("otp_push_token_required", "تعذر تجهيز إشعار رمز التحقق. فعّل إشعارات التطبيق ثم حاول مجددًا.");
        if (pushToken.Length is < 20 or > 4096 || pushToken.Any(c => c < '!' || c > '~'))
            return Fail("invalid_otp_push_token", "معرّف إشعارات التطبيق غير صالح. أعد تجهيز الإشعارات ثم حاول مجددًا.");

        token.ThrowIfCancellationRequested();
        var challenge = store.CreateForDelivery(destination, purpose) with {
            FcmToken = pushToken, ClientType = client,
            NotificationRenderer = notificationRenderer == AndroidNotificationRendering.AppRenderer ? notificationRenderer : null
        };
        try
        {
            using var deliveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            deliveryTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var messageId = await sender.SendTestOtpAsync(challenge, deliveryTimeout.Token)
                .WaitAsync(deliveryTimeout.Token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(messageId)) throw new InvalidOperationException("Push provider did not accept the message.");
            return new(challenge.ChallengeId, 300, null, "تم إرسال رمز التحقق عبر إشعار التطبيق للاختبار.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            store.CancelChallenge(challenge.ChallengeId);
            throw;
        }
        catch (Exception)
        {
            store.CancelChallenge(challenge.ChallengeId);
            return Fail("otp_push_delivery_failed", "تعذر إرسال إشعار رمز التحقق. حاول مجددًا بعد التأكد من اتصال التطبيق.");
        }
    }

    private static OtpIssueResult Fail(string code, string message) => new(null, 0, code, message);
}

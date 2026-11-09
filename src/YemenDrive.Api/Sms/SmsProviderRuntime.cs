using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YemenDrive.Api.Sms;

public sealed record SmsConnectionStatus(string State, string Message, bool Connected = false, bool RequiresActivation = false);
public sealed record SmsSendResult(bool Accepted, string ErrorCode, bool Uncertain = false);

// Only this server-side service sees the provider token. Responses never expose it.
public sealed class SmsProviderRuntime(SmsProviderConfigurationStore configuration, HttpClient http)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? fingerprint;
    private string? authToken;
    private DateTime nextLoginUtc;
    private SmsConnectionStatus status = new("unconfigured", "لم تُحفظ بيانات مزود الرسائل بعد.");

    public async Task<bool> IsConfiguredAsync(CancellationToken token) => (await configuration.GetAsync(token)).HasPassword;

    public async Task<SmsConnectionStatus> ConnectAsync(CancellationToken token, bool force = false)
    {
        await gate.WaitAsync(token);
        try { await EnsureLoginAsync(token, force); return status; }
        finally { gate.Release(); }
    }

    public async Task<SmsConnectionStatus> ActivateAsync(string? code, CancellationToken token)
    {
        if (code is null || code.Length is < 4 or > 10 || code.Any(c => c is < '0' or > '9') || !long.TryParse(code, out var numericCode))
            return new("invalid_activation_code", "أدخل رمز التفعيل المرسل من المزود بالأرقام.", RequiresActivation: true);
        await gate.WaitAsync(token);
        try
        {
            var credentials = await EnsureLoginAsync(token);
            if (credentials is null || !status.RequiresActivation || string.IsNullOrEmpty(authToken)) return status;
            using var response = await PostAsync(credentials, "authenticator", new { ActivationCode = numericCode }, authToken, token);
            if (!response.IsSuccessStatusCode || !await IsAcceptedAsync(response, token))
                return status = new("activation_required", "رمز التفعيل غير صحيح أو تعذر تفعيل الحساب. حاول مجددًا.", RequiresActivation: true);
            authToken = null;
            await EnsureLoginAsync(token, force: true);
            return status;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or ArgumentException)
        {
            token.ThrowIfCancellationRequested();
            return status = new("activation_required", "تعذر التحقق من التفعيل. حاول مجددًا.", RequiresActivation: true);
        }
        finally { gate.Release(); }
    }

    public Task<SmsSendResult> SendAsync(long id, string destination, string text, CancellationToken token) => SendAsync(id, new[] { destination }, text, token);

    public async Task<SmsSendResult> SendAsync(long id, string[] destinations, string text, CancellationToken token)
    {
        await gate.WaitAsync(token);
        var attempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var credentials = await EnsureLoginAsync(token);
            if (credentials is null || !status.Connected || authToken is null) return new(false, "sms_provider_not_ready");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                attempted = true;
                using var response = await PostAsync(credentials, "sendSMSMsg", new { Numbers = destinations, id, Text = text }, authToken, token);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    credentials = await EnsureLoginAsync(token, force: true);
                    if (credentials is null || !status.Connected || authToken is null) return new(false, "sms_provider_not_ready");
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    return new(false, "sms_provider_rejected", (int)response.StatusCode >= 500);
                return await IsAcceptedAsync(response, token) ? new(true, "") : new(false, "sms_provider_rejected");
            }
            return new(false, "sms_provider_rejected");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or ArgumentException)
        {
            // A timeout may occur after acceptance; never resend blindly.
            return new(false, "sms_delivery_unconfirmed", attempted);
        }
        finally { gate.Release(); }
    }

    private async Task<SmsProviderCredentials?> EnsureLoginAsync(CancellationToken token, bool force = false)
    {
        var credentials = await configuration.GetCredentialsAsync(token);
        var key = credentials is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials))));
        if (fingerprint != key)
        {
            fingerprint = key; authToken = null; nextLoginUtc = default;
            status = new("disconnected", "جارٍ الاتصال بمزود الرسائل.");
        }
        if (credentials is null) { status = new("unconfigured", "لم تُحفظ بيانات مزود الرسائل بعد."); return null; }
        if (!force && (status.Connected || status.RequiresActivation || DateTime.UtcNow < nextLoginUtc)) return credentials;
        authToken = null;
        nextLoginUtc = DateTime.UtcNow.AddSeconds(30);
        try
        {
            using var response = await PostAsync(credentials, "login", new
            {
                UserName = credentials.Username,
                Password = credentials.Password,
                DeviceID = credentials.DeviceNumber
            }, null, token);
            if (!response.IsSuccessStatusCode) { status = new("login_failed", "تعذر تسجيل الدخول إلى المزود. تحقق من بيانات الاتصال."); return credentials; }
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { status = new("login_failed", "أعاد المزود استجابة دخول غير صالحة."); return credentials; }
            var valid = !root.TryGetProperty("IsValid", out var isValid) || isValid.ValueKind == JsonValueKind.True;
            var type = root.TryGetProperty("Type", out var action) && action.ValueKind == JsonValueKind.Number && action.TryGetInt32(out var value) ? value : 0;
            var received = root.TryGetProperty("Token", out var receivedToken) && receivedToken.ValueKind == JsonValueKind.String ? receivedToken.GetString() : null;
            if (valid && !string.IsNullOrWhiteSpace(received) && received.Length <= 16384 && (type == 2 || type == 4))
            {
                authToken = received;
                status = type == 2 ? new("connected", "تم تسجيل الدخول إلى مزود الرسائل بنجاح.", Connected: true)
                    : new("activation_required", "أدخل رمز التفعيل الذي أرسله المزود لتفعيل الحساب.", RequiresActivation: true);
            }
            else status = type == 3 ? new("password_required", "المزود يطلب تعيين كلمة مرور. حدّثها لدى المزود ثم احفظ البيانات هنا.")
                : new("login_failed", "لم يقبل المزود بيانات تسجيل الدخول.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or ArgumentException)
        {
            token.ThrowIfCancellationRequested();
            status = new("unavailable", "تعذر الاتصال بمزود الرسائل. ستتم إعادة الاتصال تلقائيًا.");
        }
        return credentials;
    }

    private async Task<HttpResponseMessage> PostAsync(SmsProviderCredentials credentials, string method, object body, string? tokenValue, CancellationToken token)
    {
        var baseUri = new Uri(credentials.Url);
        var path = baseUri.AbsolutePath.TrimEnd('/');
        foreach (var endpoint in new[] { "/login", "/authenticator", "/sendSMSMsg", "/resetPassword" })
            if (path.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase)) { path = path[..^endpoint.Length]; break; }
        if (path.Length == 0) path = "/api";
        var uri = new UriBuilder(baseUri) { Path = path + "/" + method, Query = "", Fragment = "" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = null }) };
        if (tokenValue is not null) request.Headers.Add("token", tokenValue);
        return await http.SendAsync(request, token);
    }

    private static async Task<bool> IsAcceptedAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("Status", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Invalid provider status response.");
        return value.ValueKind == JsonValueKind.True;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using YemenDrive.Shared.Api;

namespace YemenDrive.Api.Sms;

public sealed record SmsProviderInput(string? Url, string? Username, string? Password, string? DeviceNumber);
public sealed record SmsProviderStatus(string Url, string Username, string DeviceNumber, bool HasPassword, DateTime? UpdatedAtUtc);
public sealed record SmsProviderCredentials(string Url, string Username, string Password, string DeviceNumber)
{
    public override string ToString() => "SMS provider credentials (redacted)";
}

public sealed class SmsProviderConfigurationStore(IDataProtectionProvider protection, string filePath)
{
    private readonly IDataProtector protector = protection.CreateProtector("YemenDrive.SmsProvider.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record SavedConfiguration(string Url, string Username, string Password, string DeviceNumber, DateTime UpdatedAtUtc);

    public async Task<SmsProviderStatus> GetAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return Status(await ReadAsync(token)); }
        finally { gate.Release(); }
    }

    public async Task<SmsProviderCredentials?> GetCredentialsAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var saved = await ReadAsync(token);
            return saved is null ? null : new(saved.Url, saved.Username, saved.Password, saved.DeviceNumber);
        }
        finally { gate.Release(); }
    }

    public async Task<SmsProviderStatus> SaveAsync(SmsProviderInput input, CancellationToken token)
    {
        var url = input.Url?.Trim() ?? "";
        var username = input.Username?.Trim() ?? "";
        var deviceNumber = input.DeviceNumber?.Trim() ?? "";
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ServiceException("invalid_sms_url", "أدخل رابط مزود الرسائل بصيغة http أو https.");
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256)
            throw new ServiceException("invalid_sms_username", "أدخل اسم المستخدم بما لا يتجاوز 256 حرفًا.");
        if (string.IsNullOrWhiteSpace(deviceNumber) || deviceNumber.Length > 128)
            throw new ServiceException("invalid_sms_device", "أدخل رقم الجهاز بما لا يتجاوز 128 حرفًا.");
        if (input.Password?.Length > 1024)
            throw new ServiceException("invalid_sms_password", "كلمة المرور أطول من الحد المسموح.");

        await gate.WaitAsync(token);
        try
        {
            var current = await ReadAsync(token);
            var password = string.IsNullOrEmpty(input.Password) ? current?.Password : input.Password;
            if (string.IsNullOrWhiteSpace(password))
                throw new ServiceException("sms_password_required", "أدخل كلمة المرور عند حفظ بيانات المزود لأول مرة.");
            var saved = new SavedConfiguration(url, username, password, deviceNumber, DateTime.UtcNow);
            var encrypted = protector.Protect(JsonSerializer.Serialize(saved));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, encrypted, token);
                File.Move(temporaryPath, filePath, overwrite: true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            return Status(saved);
        }
        finally { gate.Release(); }
    }

    private async Task<SavedConfiguration?> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            var encrypted = await File.ReadAllTextAsync(filePath, token);
            return JsonSerializer.Deserialize<SavedConfiguration>(protector.Unprotect(encrypted))
                ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            throw new ServiceException("sms_configuration_unreadable", "تعذر قراءة إعدادات مزود الرسائل. تحقق من مفاتيح التشفير على الخادم.");
        }
    }

    private static SmsProviderStatus Status(SavedConfiguration? saved) => saved is null
        ? new("", "", "", false, null)
        : new(saved.Url, saved.Username, saved.DeviceNumber, true, saved.UpdatedAtUtc);
}

using System.Text.RegularExpressions;

namespace YemenDrive.Api.Sms;

public static class OtpSmsFormatter
{
    public static string Format(string label, string code, string? appHash)
    {
        // Retriever requires a single app hash and a message of at most 140 UTF-8 bytes.
        // Ignore malformed optional hints so older/manual clients still receive their OTP.
        if (appHash is not null && Regex.IsMatch(appHash, @"\A[A-Za-z0-9+/]{11}\z"))
            return $"<#> يمن درايف: رمز التحقق {code}\nلا تشارك الرمز.\n{appHash}";
        return $"يمن درايف: رمز {label} هو {code}. صالح لمدة 5 دقائق. لا تشارك الرمز مع أي شخص.";
    }
}

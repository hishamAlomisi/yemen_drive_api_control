using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using eqsWallets;

namespace YemenDrive.Api.WalletPayments;

/// <summary>Converts a provider's display message into bounded customer text, never a financial decision.</summary>
public static class WalletProviderMessageFormatter
{
    private const int MaximumDisplayLength = 600;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static string? Format(WalletStepResult result, string storedStatus, string? submittedCode = null, string providerCode = "Floosak")
    {
        var providerName = WalletPaymentProviders.Name(providerCode);
        try { return FormatCore(result, storedStatus, submittedCode, providerName); }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
        { return $"تعذر عرض تفاصيل رسالة {providerName}. اتبع حالة العملية المعروضة أو تواصل مع الدعم."; }
    }

    private static string? FormatCore(WalletStepResult result, string storedStatus, string? submittedCode, string providerName)
    {
        var text = Clean(result.Message, submittedCode);
        // Technical text can still identify a known reason; it is used only for matching a fixed
        // Arabic sentence and is never interpolated, returned, logged or persisted.
        var match = MatchText(text ?? (result.Message is { Length: > 8192 } raw ? raw[..8192] : result.Message) ?? string.Empty);
        var agent = Has(match, @"\b(?:agent|merchant|operator|business)(?:'s)?(?:\s+\w+){0,2}\s+(?:balance|funds)\b|\b(?:balance|funds)(?:\s+(?:of|for))?\s+(?:the\s+)?(?:agent|merchant|operator)\b|رصيد (?:حساب )?(?:الوكيل|التاجر)|(?:حساب|محفظه) (?:الوكيل|التاجر).{0,15}(?:رصيد|كاف|اموال)|(?:الوكيل|التاجر) (?:لديه|عنده).{0,12}رصيد");
        var insufficient = result.ErrorCode == WalletErrorCode.InsufficientFunds ||
            Has(match, @"insufficient (?:available )?(?:balance|funds)|(?:balance|funds).{0,35}(?:insufficient|not (?:enough|sufficient)|too low)|not enough (?:balance|funds|money)|(?:رصيد|اموال).{0,35}(?:غير كاف|لا يكفي|لايكفي|منخفض)|عدم كفايه (?:الرصيد|الاموال)");
        if (insufficient)
            return agent ? $"أفادت {providerName} بأن رصيد حساب الوكيل لا يكفي لتنفيذ السحب. يمكنك التواصل مع دعم يمن درايف."
                : $"أفادت {providerName} بأن رصيد حسابك لا يكفي لإتمام السحب بالمبلغ والعمولة المطلوبة.";
        if (result.ErrorCode == WalletErrorCode.AuthenticationFailed ||
            Has(match, @"authentication failed|invalid (?:agent |api |client )?credentials|unauthorized|invalid (?:access )?token|بيانات دخول الوكيل|فشل (?:تسجيل الدخول|المصادقه)"))
            return $"تعذر اتصال التطبيق بحساب الوكيل لدى {providerName}. يمكنك التواصل مع دعم يمن درايف.";
        if (Has(match, @"(?:otp|pin|verification code|one.time password|رمز التحقق|رمز التاكيد|الرمز).{0,30}(?:expired|انته|منتهي)|(?:expired|انتهت صلاحيه).{0,25}(?:otp|pin|code|رمز|الرمز)"))
            return $"أفادت {providerName} بأن صلاحية رمز التحقق انتهت. اتبع حالة العملية المعروضة قبل بدء أي طلب جديد.";
        if (Has(match, @"(?:otp|pin|verification code|one.time password|رمز التحقق|رمز التاكيد|الرمز).{0,30}(?:invalid|incorrect|wrong|غير صحيح|خاطئ|خاطي)|(?:invalid|incorrect|wrong).{0,15}(?:otp|pin|code)|رمز (?:خاطئ|غير صحيح)"))
            return $"أفادت {providerName} بأن رمز التحقق غير صحيح. تحقق من الرمز المرسل إلى هاتفك.";
        if (Has(match, @"(?:account|wallet|customer|user).{0,30}(?:not found|does not exist|not exist|not registered|unregistered)|(?:الحساب|المحفظه|العميل|الرقم).{0,30}(?:غير موجود|غير مسجل|لا يوجد)|لا يوجد حساب|لم يتم العثور علي"))
            return $"لم تجد {providerName} حساباً مسجلاً برقم هاتفك. تحقق من تسجيل هذا الرقم في {providerName}.";
        if (Has(match, @"(?:account|wallet|customer).{0,30}(?:blocked|suspended|frozen|disabled)|(?:الحساب|المحفظه|حسابك).{0,30}(?:موقوف|محظور|مجمد|معطل)"))
            return $"أفادت {providerName} بأن الحساب موقوف أو مقيد. تواصل مع دعم {providerName} لإعادة تفعيله.";
        if (Has(match, @"(?:account|wallet|customer).{0,30}(?:inactive|not activ)|(?:الحساب|المحفظه|حسابك).{0,30}(?:غير نشط|غير مفعل)"))
            return $"أفادت {providerName} بأن حسابك غير مفعّل ويحتاج إلى التفعيل لدى {providerName}.";
        if (Has(match, @"\bkyc\b|identity verification|verify (?:your )?identity|استكمال.{0,20}(?:التوثيق|التحقق|البيانات)|توثيق (?:الحساب|حسابك)|التحقق من الهويه"))
            return $"تطلب {providerName} استكمال توثيق حسابك وبيانات هويتك قبل السماح بالسحب.";
        if (Has(match, @"عدد (?:مرات|محاولات).{0,25}(?:ارسال|طلب).{0,25}رمز|(?:otp|verification code|confirmation code|code).{0,30}(?:send|issuance|request).{0,30}(?:limit|exceed)|(?:limit|exceed).{0,25}(?:otp|verification code|confirmation code)"))
            return $"أفادت {providerName} بأن عدد مرات طلب أو إرسال رمز التأكيد تجاوز المسموح. اتبع حالة العملية المعروضة قبل أي محاولة أخرى.";
        if (Has(match, @"(?:daily|اليومي).{0,25}(?:limit|exceed|حد)|(?:limit|حد).{0,25}(?:daily|اليومي)"))
            return $"أفادت {providerName} بأن العملية تتجاوز الحد اليومي المسموح للسحب.";
        if (Has(match, @"(?:monthly|الشهري).{0,25}(?:limit|exceed|حد)|(?:limit|حد).{0,25}(?:monthly|الشهري)"))
            return $"أفادت {providerName} بأن العملية تتجاوز الحد الشهري المسموح للسحب.";
        if (Has(match, @"minimum amount|amount.{0,25}(?:below|less than|too small)|الحد الادني|المبلغ.{0,20}(?:اقل|صغير)"))
            return $"أفادت {providerName} بأن مبلغ العملية أقل من الحد الأدنى المسموح للسحب.";
        if (Has(match, @"maximum amount|amount.{0,25}(?:above|greater than|too large)|الحد الاقصي|المبلغ.{0,20}(?:اكبر|يتجاوز)"))
            return $"أفادت {providerName} بأن مبلغ العملية يتجاوز الحد الأقصى المسموح للسحب.";
        if (Has(match, @"(?:currency).{0,25}(?:invalid|mismatch|not supported|unsupported)|unsupported currency|العمله.{0,25}(?:غير مدعوم|غير صحيح|لا تطابق)"))
            return $"لم تقبل {providerName} عملة العملية. الدفع في يمن درايف متاح بالريال اليمني فقط.";
        if (Has(match, @"(?:phone|mobile).{0,25}(?:invalid|incorrect)|invalid (?:phone|mobile)|رقم (?:الهاتف|الجوال).{0,20}(?:غير صحيح|غير صالح)"))
            return $"أفادت {providerName} بأن رقم الهاتف غير صحيح. راجع رقم الهاتف المسجل في حسابك.";
        if (Has(match, @"maintenance|service (?:unavailable|not available)|temporarily unavailable|خارج الخدمه|صيانه|الخدمه.{0,20}غير متاح"))
            return $"خدمة {providerName} غير متاحة مؤقتاً أو تحت الصيانة.";
        if (Has(match, @"too many (?:requests|attempts)|rate limit|محاولات كثيره|عدد المحاولات|طلبات كثيره"))
            return $"أفادت {providerName} بأن عدد المحاولات تجاوز المسموح.";
        if (Has(match, @"(?:duplicate|already processed|already exists)|(?:مكرره|مكرر|سبق تنفيذ|تم تنفيذ.{0,12}سابقا)"))
            return $"أفادت {providerName} بأن هذا الطلب أُرسل أو عولج سابقاً. تحقق من حالة العملية الحالية قبل إعادة الدفع.";
        if (Has(match, @"(?:otp|verification code|one.time password).{0,25}(?:sent|send)|(?:sent).{0,20}(?:otp|code)|(?:ارسل|ارسال).{0,25}رمز"))
            return $"أفادت {providerName} بإرسال رمز التحقق إلى هاتفك. أدخل الرمز لإكمال خطوة الدفع.";
        if (Has(match, @"(?:confirm|confirmation).{0,25}(?:customer|details|information)|تاكيد.{0,20}(?:البيانات|بيانات العميل)"))
            return $"تطلب {providerName} مراجعة بيانات الحساب والمبلغ والموافقة عليها قبل متابعة الدفع.";
        if (HasSuccessClaim(match))
            return storedStatus is "Succeeded" or "ProviderSucceeded"
                ? $"أكدت {providerName} نجاح عملية السحب."
                : storedStatus is "AwaitingConfirmation" or "AwaitingCode"
                    ? $"أكدت {providerName} نجاح الخطوة الحالية. يلزم إكمال الخطوة المعروضة لإتمام الدفع."
                    : $"وصل رد من {providerName}، لكن لم تتأكد مطابقة نتيجة الخصم بعد. لا تكرر الدفع حتى تتضح نتيجة العملية.";

        // A generic error code must not erase a more specific, safe business message.
        if (!string.IsNullOrWhiteSpace(text) && !Has(match, @"^(?:error|failed|operation failed|request failed|rejected|invalid request|فشل|خطا|فشل الطلب|فشلت العمليه)[.!: ]*$"))
            return Has(text, @"[\u0600-\u06ff]")
                ? Limit($"أفادت {providerName}: " + text)
                : Limit($"أرسلت {providerName} تفاصيل عن هذه الخطوة. النص الوارد من المزود: " + text);
        return result.ErrorCode switch
        {
            WalletErrorCode.LimitExceeded => $"أفادت {providerName} بأن العملية تتجاوز حدود السحب المسموح بها لحسابك.",
            WalletErrorCode.Rejected => $"رفضت {providerName} تنفيذ طلب السحب. راجع حالة حسابك أو تواصل مع دعم {providerName}.",
            WalletErrorCode.InvalidRequest => $"لم تقبل {providerName} بيانات الطلب. راجع البيانات المطلوبة في الخطوة الحالية.",
            WalletErrorCode.WorkflowExpired or WalletErrorCode.WorkflowNotFound => $"انتهت صلاحية جلسة الدفع لدى {providerName} أو لم تعد متاحة.",
            WalletErrorCode.Timeout => $"لم يصل رد {providerName} خلال الوقت المتوقع. اتبع حالة العملية المعروضة لمعرفة الخطوة التالية.",
            WalletErrorCode.ConnectionFailed or WalletErrorCode.ProviderUnavailable => $"تعذر الاتصال بخدمة {providerName} حالياً. اتبع حالة العملية المعروضة قبل إعادة المحاولة.",
            WalletErrorCode.OutcomeUnknown => $"لم تؤكد {providerName} نتيجة الخصم بشكل نهائي. يلزم التحقق من العملية قبل إعادة الدفع.",
            WalletErrorCode.InvalidProviderResponse => $"أرسلت {providerName} استجابة تعذر التحقق منها. اتبع حالة العملية المعروضة.",
            WalletErrorCode.CanceledBeforeSend => $"أُلغي طلب {providerName} قبل إرساله للتنفيذ.",
            WalletErrorCode.DuplicateRequest => $"أفادت {providerName} بأن هذا الطلب سبق إرساله. تحقق من حالة العملية الحالية.",
            _ => string.IsNullOrWhiteSpace(result.Message) ? null : $"تعذر عرض التفاصيل التقنية التي أرسلتها {providerName}. اتبع حالة العملية المعروضة أو تواصل مع الدعم."
        };
    }

    private static string? Clean(string? raw, string? submittedCode)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Length > 8192 ? raw[..8192] : raw;
        if (value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 8 });
                value = FindMessage(document.RootElement, 0) ?? string.Empty;
            }
            catch (JsonException) { return null; }
        }
        value = WebUtility.HtmlDecode(value).Normalize(NormalizationForm.FormKC);
        value = new string(value.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c))
            .Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
        if (!string.IsNullOrWhiteSpace(submittedCode))
        {
            value = value.Replace(submittedCode.Trim(), "[محجوب]", StringComparison.Ordinal);
            value = value.Replace(AsciiDigits(submittedCode.Trim()), "[محجوب]", StringComparison.Ordinal);
        }
        // Strip sensitive labels with actual values, but keep business text such as 'invalid OTP'.
        value = Replace(value, @"(?i)\b(?:password|passwd|pwd|secret|token|credentials|client[_ -]?secret|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|authorization|bearer|signature|session[_ -]?id)\b\s*(?:[:=]|is\b)?\s*[""']?[^\s,;""']+", "[بيانات محجوبة]");
        value = Replace(value, @"(?:كلمه المرور|كلمة المرور|المفتاح السري|رمز الدخول)\s*[:=]\s*[^\s,؛]+", "[بيانات محجوبة]");
        value = Replace(value, @"(?i)(?:\b(?:otp|pin|verification code|confirmation code|code)\b|رمز التحقق|رمز التأكيد)\s*[:=]?\s*[\p{N}]{4,10}\b", "رمز التحقق [محجوب]");
        value = Replace(value, @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)?\b|\b[A-Za-z0-9+/_=-]{28,}\b", "[محجوب]");
        var numericSource = value;
        value = Regex.Replace(value, @"(?<!\p{N})\p{N}{6,}(?!\p{N})", number =>
        {
            var before = numericSource[..number.Index];
            var after = numericSource[(number.Index + number.Length)..];
            var money = number.Length <= 12 && (Has(after, @"^\s*(?:YER|SAR|USD|ريال|ر\.?\s?ي|دولار)(?:\b|\s|$)") ||
                Has(before, @"(?:\bYER|\bSAR|\bUSD|ريال|دولار)\s*$"));
            return money ? number.Value : "[محجوب]";
        }, RegexOptions.CultureInvariant, RegexTimeout);
        if (Has(value, @"(?i)(?:https?://|www\.|[A-Z]:\\|/api/|/usr/|/var/|<script|<html|<!doctype|\bSystem\.[A-Za-z]|\bat [A-Za-z0-9_.]+\(|\b(?:stack ?trace|SqlException|NullReferenceException|SELECT\s+.+\s+FROM|INSERT\s+INTO)\b|[{}])"))
            return null;
        value = Replace(value, @"<[^>]{0,200}>", " ");
        value = Replace(value, @"\s+", " ").Trim();
        if (Has(value, @"^[A-Z][A-Z0-9_]{4,}$") && value.Contains('_')) return null;
        return string.IsNullOrWhiteSpace(value) ? null : Limit(value);
    }

    private static string? FindMessage(JsonElement element, int depth)
    {
        if (depth > 3 || element.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "message", "error_message", "error_description", "description", "detail", "error" })
        {
            var property = element.EnumerateObject().FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
            if (property.Value.ValueKind == JsonValueKind.Object && FindMessage(property.Value, depth + 1) is { } child) return child;
        }
        return null;
    }

    private static string MatchText(string text) => Replace(text.Normalize(NormalizationForm.FormD), @"\p{Mn}", "")
        .Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي').Replace('_', ' ').ToLowerInvariant();
    private static string AsciiDigits(string text) => string.Concat(text.Select(c => char.IsDigit(c) ? ((int)char.GetNumericValue(c)).ToString(CultureInfo.InvariantCulture) : c.ToString()));
    private static bool HasSuccessClaim(string text) => !Has(text, @"not (?:successful|completed|paid)|unsuccessful|لم (?:تنجح|ينجح|يتم|تتم)|غير ناجح|فشل") &&
        Has(text, @"\b(?:success(?:ful(?:ly)?)?|succeeded|completed|paid|withdrawn|debited)\b|بنجاح|تم(?:ت)? (?:الدفع|دفع|الخصم|خصم|السحب|سحب)|نجحت العمليه");
    private static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static string Replace(string text, string pattern, string replacement) => Regex.Replace(text, pattern, replacement, RegexOptions.CultureInvariant, RegexTimeout);
    private static string Limit(string text) => text.Length <= MaximumDisplayLength ? text : text[..(MaximumDisplayLength - 1)] + "…";
}

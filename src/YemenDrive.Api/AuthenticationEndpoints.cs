using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Accounting;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api;

public static class AuthenticationEndpoints
{
    public static void MapAuthenticationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/register", async (RegisterRequest request, YemenDriveDbContext db, OtpChallengeStore otpStore, FinancialAccountProvisioningService financialAccounts, CancellationToken token) =>
        {
            var errors = ValidateRegistration(request);
            if (errors.Count > 0) return Results.Ok(ApiResult.Invalid(errors));

            var phone = request.PhoneNumber!.Trim();
            if (string.IsNullOrWhiteSpace(request.VerificationToken) || !otpStore.ConsumeVerificationToken(request.VerificationToken, phone, "signUp"))
                return Results.Ok(ApiResult.Fail("otp_required", "يجب التحقق من رمز OTP قبل إنشاء الحساب."));
            if (await db.Users.AnyAsync(x => x.PhoneNumber == phone, token))
                return Results.Ok(ApiResult.Fail("user_exists", "رقم الهاتف مسجل مسبقاً."));

            var user = new User
            {
                PhoneNumber = phone,
                DisplayName = request.DisplayName!.Trim(),
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant(),
                PasswordHash = PasswordHash.Create(request.Password!),
                Role = UserRole.Customer,
                IsActive = true,
                Gender = request.Gender,
                Street = request.Street,
                City = request.City,
                District = request.District,
                Wallet = new Wallet { Currency = "YER" }
            };
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            db.Users.Add(user);
            await db.SaveChangesAsync(token);
            await financialAccounts.ProvisionUserAsync(user, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Results.Ok(ApiResult.Ok(Session(user), "تم إنشاء الحساب وتسجيل الدخول بنجاح."));
        });

        app.MapPost("/api/auth/login", async (LoginRequest request, YemenDriveDbContext db, OtpChallengeStore otpStore, CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(request.PhoneNumber) || string.IsNullOrWhiteSpace(request.Password))
                return Results.Ok(ApiResult.Fail("invalid_credentials", "رقم الهاتف وكلمة المرور مطلوبان."));

            var user = await db.Users.SingleOrDefaultAsync(x => x.PhoneNumber == request.PhoneNumber.Trim(), token);
            if (user is null || !user.IsActive || !PasswordHash.Verify(request.Password, user.PasswordHash))
                return Results.Ok(ApiResult.Fail("invalid_credentials", "رقم الهاتف أو كلمة المرور غير صحيحة."));

            if (!request.IsTrustedDevice)
            {
                var challenge = otpStore.Create(user.PhoneNumber, "signIn");
                return Results.Ok(ApiResult.Ok(new { requiresOtp = true, challengeId = challenge.ChallengeId, expiresInSeconds = challenge.ExpiresInSeconds }, "تم إرسال رمز التحقق إلى الهاتف."));
            }

            return Results.Ok(ApiResult.Ok(Session(user), "تم تسجيل الدخول بنجاح."));
        });

        app.MapPost("/api/admin/login", async (AdminLoginRequest request, YemenDriveDbContext db, CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(request.PhoneNumber) || string.IsNullOrWhiteSpace(request.Password))
                return Results.Ok(ApiResult.Fail("invalid_admin_credentials", "رقم هاتف الإدارة وكلمة المرور مطلوبان."));

            var user = await db.Users.SingleOrDefaultAsync(x =>
                x.PhoneNumber == request.PhoneNumber.Trim() && x.Role == UserRole.Admin && x.IsActive, token);
            if (user is null || !PasswordHash.Verify(request.Password, user.PasswordHash))
                return Results.Ok(ApiResult.Fail("invalid_admin_credentials", "بيانات حساب الإدارة غير صحيحة."));

            return Results.Ok(ApiResult.Ok(Session(user), "تم تسجيل الدخول إلى لوحة التحكم."));
        });

        app.MapPost("/api/auth/register/request-otp", (SignUpOtpRequest request, OtpChallengeStore otpStore) =>
        {
            if (string.IsNullOrWhiteSpace(request.PhoneNumber))
                return Results.Ok(ApiResult.Fail("phone_required", "رقم الهاتف مطلوب."));
            var challenge = otpStore.Create(request.PhoneNumber, "signUp");
            return Results.Ok(ApiResult.Ok(new { challengeId = challenge.ChallengeId, expiresInSeconds = challenge.ExpiresInSeconds }, "تم إنشاء رمز التحقق. سيتم إرساله عبر مزود الرسائل لاحقاً."));
        });

        app.MapPost("/api/auth/sign-in/verify-device", async (DeviceOtpRequest request, OtpChallengeStore otpStore, YemenDriveDbContext db, CancellationToken token) =>
        {
            var verification = otpStore.Verify(request.ChallengeId, request.PhoneNumber ?? string.Empty, request.Code ?? string.Empty, "signIn");
            if (verification is null)
                return Results.Ok(ApiResult.Fail("invalid_otp", "رمز التحقق غير صحيح أو منتهي."));
            var user = await db.Users.SingleOrDefaultAsync(x => x.PhoneNumber == request.PhoneNumber!.Trim(), token);
            return user is null
                ? Results.Ok(ApiResult.Fail("user_not_found", "الحساب غير موجود."))
                : Results.Ok(ApiResult.Ok(Session(user), "تم التحقق من الجهاز وتسجيل الدخول."));
        });

        app.MapPost("/api/auth/verify-otp", (GenericOtpRequest request, OtpChallengeStore otpStore) =>
        {
            var purpose = string.IsNullOrWhiteSpace(request.Purpose) ? "signUp" : request.Purpose;
            var token = otpStore.Verify(request.ChallengeId, request.Destination ?? string.Empty, request.Code ?? string.Empty, purpose!);
            return token is null
                ? Results.Ok(ApiResult.Fail("invalid_otp", "رمز التحقق غير صحيح أو منتهي."))
                : Results.Ok(ApiResult.Ok(new { verificationToken = token }, "تم التحقق من الرمز."));
        });

        app.MapPost("/api/auth/refresh", async (RefreshRequest request, YemenDriveDbContext db, CancellationToken token) =>
        {
            const string prefix = "demo-refresh-";
            if (string.IsNullOrWhiteSpace(request.RefreshToken) ||
                !request.RefreshToken.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(request.RefreshToken[prefix.Length..], out var userId))
            {
                return Results.Ok(ApiResult.Fail("invalid_refresh_token", "رمز التجديد غير صالح."));
            }

            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, token);
            return user is null
                ? Results.Ok(ApiResult.Fail("invalid_refresh_token", "رمز التجديد غير صالح."))
                : Results.Ok(ApiResult.Ok(Session(user), "تم تجديد جلسة الدخول."));
        });

        app.MapPost("/api/auth/password/request-reset", (PasswordResetRequest request, OtpChallengeStore otpStore) =>
        {
            if (string.IsNullOrWhiteSpace(request.Identity))
                return Results.Ok(ApiResult.Fail("identity_required", "رقم الهاتف أو البريد الإلكتروني مطلوب."));
            var challenge = otpStore.Create(request.Identity, "passwordReset");
            return Results.Ok(ApiResult.Ok(new { challengeId = challenge.ChallengeId, expiresInSeconds = challenge.ExpiresInSeconds }, "تم إنشاء رمز إعادة التعيين وسيتم إرساله عبر مزود الرسائل لاحقاً."));
        });

        app.MapPost("/api/auth/password/reset", async (PasswordResetCompleteRequest request, OtpChallengeStore otpStore, YemenDriveDbContext db, CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
                return Results.Ok(ApiResult.Fail("invalid_password", "كلمة المرور يجب ألا تقل عن 8 أحرف."));
            if (string.IsNullOrWhiteSpace(request.Identity) || string.IsNullOrWhiteSpace(request.VerificationToken) ||
                !otpStore.ConsumeVerificationToken(request.VerificationToken, request.Identity, "passwordReset"))
                return Results.Ok(ApiResult.Fail("otp_required", "يجب التحقق من رمز OTP قبل تغيير كلمة المرور."));
            var identity = request.Identity.Trim();
            var user = await db.Users.SingleOrDefaultAsync(x => x.PhoneNumber == identity || x.Email == identity.ToLowerInvariant(), token);
            if (user is null) return Results.Ok(ApiResult.Fail("user_not_found", "الحساب غير موجود."));
            user.PasswordHash = PasswordHash.Create(request.NewPassword);
            user.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(token);
            return Results.Ok(ApiResult.Ok(null, "تم تغيير كلمة المرور بنجاح."));
        });

        app.MapPost("/api/auth/password/change", async (
            ChangePasswordRequest request,
            ICurrentUserContext currentUser,
            YemenDriveDbContext db,
            CancellationToken token) =>
        {
            if (currentUser.UserId is not int userId)
                return Results.Ok(ApiResult.Fail("authentication_required", "يجب تسجيل الدخول لتغيير كلمة المرور."));
            if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
                string.IsNullOrWhiteSpace(request.NewPassword) ||
                request.NewPassword.Length < 8)
                return Results.Ok(ApiResult.Fail("invalid_password", "كلمة المرور الجديدة يجب ألا تقل عن 8 أحرف."));

            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, token);
            if (user is null)
                return Results.Ok(ApiResult.Fail("user_not_found", "الحساب غير موجود."));
            if (!PasswordHash.Verify(request.CurrentPassword, user.PasswordHash))
                return Results.Ok(ApiResult.Fail("invalid_current_password", "كلمة المرور الحالية غير صحيحة."));

            user.PasswordHash = PasswordHash.Create(request.NewPassword);
            user.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(token);
            return Results.Ok(ApiResult.Ok(null, "تم تغيير كلمة المرور بنجاح."));
        });
    }

    private static IReadOnlyCollection<ValidationError> ValidateRegistration(RegisterRequest request)
    {
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(request.DisplayName)) errors.Add(new("displayName", "اسم المستخدم مطلوب."));
        if (string.IsNullOrWhiteSpace(request.PhoneNumber) || request.PhoneNumber.Trim().Length < 7) errors.Add(new("phoneNumber", "رقم الهاتف غير صحيح."));
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8) errors.Add(new("password", "كلمة المرور يجب ألا تقل عن 8 أحرف."));
        return errors;
    }

    private static object Session(User user) => new
    {
        accessToken = $"demo-access-{user.Id}",
        refreshToken = $"demo-refresh-{user.Id}",
        user = new { id = user.Id, phoneNumber = user.PhoneNumber, displayName = user.DisplayName, role = user.Role.ToString() }
    };

    public sealed record RegisterRequest(string? PhoneNumber, string? DisplayName, string? Password, string? Email = null, string? Gender = null, string? Street = null, string? City = null, string? District = null, string? VerificationToken = null);
    public sealed record LoginRequest(string? PhoneNumber, string? Password, string? DeviceId = null, bool IsTrustedDevice = false);
    public sealed record AdminLoginRequest(string? PhoneNumber, string? Password);
    public sealed record SignUpOtpRequest(string? PhoneNumber);
    public sealed record DeviceOtpRequest(string? PhoneNumber, string? Code, string? ChallengeId, string? DeviceId);
    public sealed record GenericOtpRequest(string? Destination, string? Code, string? Purpose, string? ChallengeId = null);
    public sealed record RefreshRequest(string? RefreshToken);
    public sealed record PasswordResetRequest(string? Identity, string? Channel);
    public sealed record PasswordResetCompleteRequest(string? Identity, string? NewPassword, string? VerificationToken);
    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
}

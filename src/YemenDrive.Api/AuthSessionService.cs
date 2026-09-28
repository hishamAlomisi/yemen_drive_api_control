using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

public sealed class AuthSessionService
{
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CustomerSessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan AdminSessionLifetime = TimeSpan.FromHours(8);

    public async Task<SessionTokens> CreateAsync(
        YemenDriveDbContext db,
        User user,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.AuthSessions
            .Where(item => item.SessionExpiresAtUtc <= now.AddDays(-7))
            .ExecuteDeleteAsync(cancellationToken);

        var familyId = Guid.NewGuid();
        var tokens = NewTokens();
        var sessionExpiresAtUtc = now.Add(user.Role == UserRole.Admin
            ? AdminSessionLifetime
            : CustomerSessionLifetime);
        db.AuthSessions.Add(new AuthSession
        {
            FamilyId = familyId,
            UserId = user.Id,
            DeviceId = deviceId,
            AccessTokenHash = Hash(tokens.AccessToken),
            RefreshTokenHash = Hash(tokens.RefreshToken),
            AccessExpiresAtUtc = Min(now.Add(AccessTokenLifetime), sessionExpiresAtUtc),
            SessionExpiresAtUtc = sessionExpiresAtUtc
        });
        await db.SaveChangesAsync(cancellationToken);
        return tokens with
        {
            AccessExpiresAtUtc = Min(now.Add(AccessTokenLifetime), sessionExpiresAtUtc),
            SessionExpiresAtUtc = sessionExpiresAtUtc
        };
    }

    public async Task<AuthenticatedSession?> AuthenticateAsync(
        YemenDriveDbContext db,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        var now = DateTime.UtcNow;
        var session = await db.AuthSessions.AsNoTracking()
            .Where(item => item.AccessTokenHash == Hash(accessToken) &&
                           item.RotatedAtUtc == null &&
                           item.RevokedAtUtc == null &&
                           item.AccessExpiresAtUtc > now &&
                           item.SessionExpiresAtUtc > now &&
                           item.User.IsActive)
            .Select(item => new AuthenticatedSession(item.UserId, item.FamilyId))
            .SingleOrDefaultAsync(cancellationToken);
        return session;
    }

    public async Task<RefreshedSession?> RefreshAsync(
        YemenDriveDbContext db,
        string? refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;
        var now = DateTime.UtcNow;
        var refreshHash = Hash(refreshToken);
        var existing = await db.AuthSessions.AsNoTracking()
            .Include(item => item.User)
            .SingleOrDefaultAsync(item => item.RefreshTokenHash == refreshHash, cancellationToken);
        if (existing is null || existing.RevokedAtUtc is not null) return null;

        if (existing.RotatedAtUtc is not null)
        {
            if (existing.SessionExpiresAtUtc > now)
                await RevokeFamilyAsync(db, existing.FamilyId, now, cancellationToken);
            return null;
        }
        if (existing.SessionExpiresAtUtc <= now || !existing.User.IsActive)
        {
            if (!existing.User.IsActive)
                await RevokeFamilyAsync(db, existing.FamilyId, now, cancellationToken);
            return null;
        }

        var tokens = NewTokens();
        var sessionExpiresAtUtc = DateTime.SpecifyKind(existing.SessionExpiresAtUtc, DateTimeKind.Utc);
        var accessExpiresAtUtc = Min(now.Add(AccessTokenLifetime), sessionExpiresAtUtc);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await db.AuthSessions
            .Where(item => item.Id == existing.Id &&
                           item.RotatedAtUtc == null &&
                           item.RevokedAtUtc == null &&
                           item.SessionExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.RotatedAtUtc, now)
                .SetProperty(item => item.UpdatedAtUtc, now), cancellationToken);

        if (claimed == 0)
        {
            var current = await db.AuthSessions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == existing.Id, cancellationToken);
            if (current?.RotatedAtUtc is not null && current.SessionExpiresAtUtc > now)
                await RevokeFamilyAsync(db, existing.FamilyId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        db.AuthSessions.Add(new AuthSession
        {
            FamilyId = existing.FamilyId,
            UserId = existing.UserId,
            DeviceId = existing.DeviceId,
            AccessTokenHash = Hash(tokens.AccessToken),
            RefreshTokenHash = Hash(tokens.RefreshToken),
            AccessExpiresAtUtc = accessExpiresAtUtc,
            SessionExpiresAtUtc = sessionExpiresAtUtc
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RefreshedSession(existing.User, tokens with
        {
            AccessExpiresAtUtc = accessExpiresAtUtc,
            SessionExpiresAtUtc = sessionExpiresAtUtc
        });
    }

    public async Task RevokeCurrentAsync(
        YemenDriveDbContext db,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return;
        var session = await db.AuthSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AccessTokenHash == Hash(accessToken) &&
                                          item.RotatedAtUtc == null &&
                                          item.RevokedAtUtc == null,
                cancellationToken);
        if (session is not null)
            await RevokeFamilyAsync(db, session.FamilyId, DateTime.UtcNow, cancellationToken);
    }

    public async Task RevokeAllForUserAsync(
        YemenDriveDbContext db,
        int userId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.AuthSessions
            .Where(item => item.UserId == userId && item.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.RevokedAtUtc, now)
                .SetProperty(item => item.UpdatedAtUtc, now), cancellationToken);
    }

    private static Task<int> RevokeFamilyAsync(
        YemenDriveDbContext db,
        Guid familyId,
        DateTime now,
        CancellationToken cancellationToken) =>
        db.AuthSessions
            .Where(item => item.FamilyId == familyId && item.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.RevokedAtUtc, now)
                .SetProperty(item => item.UpdatedAtUtc, now), cancellationToken);

    private static SessionTokens NewTokens() => new(NewOpaqueToken(), NewOpaqueToken());

    private static string NewOpaqueToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
}

public sealed record SessionTokens(
    string AccessToken,
    string RefreshToken,
    DateTime AccessExpiresAtUtc = default,
    DateTime SessionExpiresAtUtc = default);

public sealed record AuthenticatedSession(int UserId, Guid FamilyId);

public sealed record RefreshedSession(User User, SessionTokens Tokens);

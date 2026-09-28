using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

public sealed class TrustedDeviceCredentialService
{
    private static readonly TimeSpan CredentialLifetime = TimeSpan.FromDays(90);

    public static bool IsValidDeviceId(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) &&
        deviceId.Length is >= 8 and <= 128 &&
        deviceId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public async Task<string> IssueAsync(
        YemenDriveDbContext db,
        int userId,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var tokenHash = Hash(rawToken);
        var trustedDevice = await db.TrustedDevices.SingleOrDefaultAsync(
            item => item.UserId == userId && item.DeviceId == deviceId,
            cancellationToken);

        if (trustedDevice is null)
        {
            trustedDevice = new TrustedDevice
            {
                UserId = userId,
                DeviceId = deviceId,
                TokenHash = tokenHash,
                ExpiresAtUtc = now.Add(CredentialLifetime),
                LastUsedAtUtc = now
            };
            db.TrustedDevices.Add(trustedDevice);
        }
        else
        {
            trustedDevice.TokenHash = tokenHash;
            trustedDevice.ExpiresAtUtc = now.Add(CredentialLifetime);
            trustedDevice.LastUsedAtUtc = now;
            trustedDevice.RevokedAtUtc = null;
            trustedDevice.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return rawToken;
    }

    public async Task<bool> IsTrustedAsync(
        YemenDriveDbContext db,
        int userId,
        string? deviceId,
        string? rawToken,
        CancellationToken cancellationToken)
    {
        if (!IsValidDeviceId(deviceId) || string.IsNullOrWhiteSpace(rawToken)) return false;

        var trustedDevice = await db.TrustedDevices.SingleOrDefaultAsync(
            item => item.UserId == userId && item.DeviceId == deviceId,
            cancellationToken);
        var now = DateTime.UtcNow;
        if (trustedDevice is null || trustedDevice.RevokedAtUtc is not null || trustedDevice.ExpiresAtUtc <= now)
            return false;

        var expectedHash = Encoding.ASCII.GetBytes(trustedDevice.TokenHash);
        var suppliedHash = Encoding.ASCII.GetBytes(Hash(rawToken));
        if (expectedHash.Length != suppliedHash.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash))
            return false;

        trustedDevice.LastUsedAtUtc = now;
        trustedDevice.ExpiresAtUtc = now.Add(CredentialLifetime);
        trustedDevice.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task RevokeAllAsync(
        YemenDriveDbContext db,
        int userId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var devices = await db.TrustedDevices
            .Where(item => item.UserId == userId && item.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var device in devices)
        {
            device.RevokedAtUtc = now;
            device.UpdatedAtUtc = now;
        }
    }

    private static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
}

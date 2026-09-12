using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace YemenDrive.Api;

public sealed class OtpChallengeStore(ILogger<OtpChallengeStore> logger)
{
    private sealed record Challenge(string Id, string Destination, string Purpose, string Code, string VerificationToken, DateTime ExpiresAtUtc);
    private readonly ConcurrentDictionary<string, Challenge> _challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Destination, string Purpose, DateTime ExpiresAtUtc)> _verified = new(StringComparer.Ordinal);

    public (string ChallengeId, int ExpiresInSeconds) Create(string destination, string purpose)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var challenge = new Challenge(id, destination.Trim(), purpose.Trim(), code, token, DateTime.UtcNow.AddMinutes(5));
        _challenges[id] = challenge;
        logger.LogInformation("YemenDrive OTP for {Destination} ({Purpose}): {Code}", challenge.Destination, challenge.Purpose, challenge.Code);
        Console.WriteLine($"[YemenDrive OTP] {challenge.Purpose} -> {challenge.Destination}: {challenge.Code}");
        return (id, 300);
    }

    public string? Verify(string? challengeId, string destination, string code, string purpose)
    {
        Challenge? challenge = null;
        if (!string.IsNullOrWhiteSpace(challengeId))
            _challenges.TryGetValue(challengeId, out challenge);
        else
            challenge = _challenges.Values
                .Where(x => x.Destination == destination.Trim() && x.Purpose == purpose.Trim())
                .OrderByDescending(x => x.ExpiresAtUtc)
                .FirstOrDefault();

        if (challenge is null || challenge.ExpiresAtUtc < DateTime.UtcNow ||
            !string.Equals(challenge.Destination, destination.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(challenge.Purpose, purpose.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(challenge.Code),
                System.Text.Encoding.UTF8.GetBytes(code.Trim())))
            return null;

        _challenges.TryRemove(challenge.Id, out _);
        _verified[challenge.VerificationToken] = (challenge.Destination, challenge.Purpose, DateTime.UtcNow.AddMinutes(10));
        return challenge.VerificationToken;
    }

    public bool ConsumeVerificationToken(string token, string destination, string purpose)
    {
        if (!_verified.TryRemove(token.Trim(), out var value)) return false;
        return value.ExpiresAtUtc >= DateTime.UtcNow &&
               string.Equals(value.Destination, destination.Trim(), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(value.Purpose, purpose.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

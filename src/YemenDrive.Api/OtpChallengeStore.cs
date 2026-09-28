using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace YemenDrive.Api;

public sealed class OtpChallengeStore(bool isDevelopment)
{
    private sealed record Challenge(string Id, string Destination, string Purpose, string Code, string VerificationToken, DateTime ExpiresAtUtc, int FailedAttempts = 0);
    private const int MaximumFailedAttempts = 5;
    private readonly ConcurrentDictionary<string, Challenge> _challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Destination, string Purpose, DateTime ExpiresAtUtc)> _verified = new(StringComparer.Ordinal);

    public (string ChallengeId, int ExpiresInSeconds)? TryCreate(string destination, string purpose)
    {
        if (!isDevelopment) return null;

        var now = DateTime.UtcNow;
        foreach (var expired in _challenges.Where(item => item.Value.ExpiresAtUtc < now))
            _challenges.TryRemove(expired.Key, out _);
        foreach (var expired in _verified.Where(item => item.Value.ExpiresAtUtc < now))
            _verified.TryRemove(expired.Key, out _);

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var challenge = new Challenge(id, destination.Trim(), purpose.Trim(), code, token, now.AddMinutes(5));
        _challenges[id] = challenge;
        Console.WriteLine($"[YemenDrive OTP] {challenge.Purpose} -> {challenge.Destination}: {challenge.Code}");
        return (id, 300);
    }

    public string? Verify(string? challengeId, string destination, string code, string purpose)
    {
        string? selectedChallengeId = challengeId;
        if (!string.IsNullOrWhiteSpace(challengeId))
        {
            if (!_challenges.ContainsKey(challengeId)) return null;
        }
        else
            selectedChallengeId = _challenges.Values
                .Where(x => x.Destination == destination.Trim() && x.Purpose == purpose.Trim())
                .OrderByDescending(x => x.ExpiresAtUtc)
                .FirstOrDefault()?.Id;

        if (selectedChallengeId is null) return null;
        while (_challenges.TryGetValue(selectedChallengeId, out var challenge))
        {
            if (challenge.ExpiresAtUtc < DateTime.UtcNow)
            {
                _challenges.TryRemove(challenge.Id, out _);
                return null;
            }
            if (!string.Equals(challenge.Destination, destination.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(challenge.Purpose, purpose.Trim(), StringComparison.OrdinalIgnoreCase))
                return null;

            var codeMatches = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(challenge.Code),
                System.Text.Encoding.UTF8.GetBytes(code.Trim()));
            if (!codeMatches)
            {
                if (challenge.FailedAttempts + 1 >= MaximumFailedAttempts)
                {
                    if (_challenges.TryRemove(challenge.Id, out _)) return null;
                    continue;
                }

                var updated = challenge with { FailedAttempts = challenge.FailedAttempts + 1 };
                if (_challenges.TryUpdate(challenge.Id, updated, challenge)) return null;
                continue;
            }

            if (!_challenges.TryRemove(challenge.Id, out _)) continue;
            _verified[challenge.VerificationToken] = (challenge.Destination, challenge.Purpose, DateTime.UtcNow.AddMinutes(10));
            return challenge.VerificationToken;
        }
        return null;
    }

    public bool ConsumeVerificationToken(string token, string destination, string purpose)
    {
        if (!_verified.TryRemove(token.Trim(), out var value)) return false;
        return value.ExpiresAtUtc >= DateTime.UtcNow &&
               string.Equals(value.Destination, destination.Trim(), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(value.Purpose, purpose.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

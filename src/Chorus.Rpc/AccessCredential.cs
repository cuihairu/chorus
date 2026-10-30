using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace Chorus.Rpc;

/// <summary>
/// Short-lived Chorus access credential issued after Login/Resume and validated by
/// internal services before trusting a streamer action. Carries identity only; the
/// signing key never leaves the issuing host.
/// </summary>
public static class ChorusAccessCredential
{
    private sealed record TokenPayload(string StreamerId, string SessionId, long SessionVersion, long ExpUnixMs);

    public static string Issue(byte[] signingKey, string streamerId, string sessionId, long sessionVersion,
        DateTimeOffset expiresAtUtc, Func<DateTimeOffset>? clock = null)
    {
        RequireKey(signingKey);
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        if (expiresAtUtc <= now) throw new ArgumentException("Access credential would be issued already expired.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new TokenPayload(streamerId, sessionId, sessionVersion, expiresAtUtc.ToUnixTimeMilliseconds()));
        return $"{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(HMACSHA256.HashData(signingKey, payload))}";
    }

    /// <summary>Returns the trusted identity when the credential is authentic and unexpired; otherwise null.</summary>
    public static (string StreamerId, string SessionId, long SessionVersion)? Validate(byte[] signingKey,
        string credential, Func<DateTimeOffset>? clock = null)
    {
        RequireKey(signingKey);
        var parts = credential.Split('.', 2);
        if (parts.Length != 2) return null;
        byte[] payload, signature;
        try
        {
            payload = Base64Url.DecodeFromChars(parts[0]);
            signature = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(signingKey, payload), signature)) return null;
        TokenPayload token;
        try { token = JsonSerializer.Deserialize<TokenPayload>(payload); }
        catch (JsonException) { return null; }
        if (token is null || token.ExpUnixMs <= (clock ?? (() => DateTimeOffset.UtcNow))().ToUnixTimeMilliseconds())
            return null;
        return (token.StreamerId, token.SessionId, token.SessionVersion);
    }

    private static void RequireKey(byte[] signingKey)
    {
        if (signingKey.Length < 32) throw new InvalidOperationException("The access credential key must be at least 256 bits.");
    }
}

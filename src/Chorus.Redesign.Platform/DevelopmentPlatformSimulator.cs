using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chorus.Redesign.Platform;

/// <summary>
/// Development-only launch verification for local and contract tests. Credentials are HMAC-SHA256
/// signed JSON issued by <see cref="IssueCredential"/>; it is never a production adapter and must
/// not be enabled outside development hosts.
/// </summary>
public sealed class DevelopmentPlatformSimulator(byte[] signingKey, Func<DateTimeOffset>? clock = null)
    : IPlatformLaunchAdapter
{
    public const string PlatformName = "simulator";

    private sealed record CredentialPayload(string AppId, string OpenId, string LiveKey, long Iat, long Exp, string Nonce);

    public string Platform => PlatformName;

    public PlatformLaunchCapabilities Capabilities { get; } =
        new(PlatformName, ProductionReady: false, Evidence: "development-simulator-only");

    /// <summary>Issues a credential the simulator will verify; exists for development hosts and tests.</summary>
    public string IssueCredential(string appId, string openId, string liveKey, TimeSpan lifetime, string? nonce = null)
    {
        if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("AppId is required.", nameof(appId));
        if (string.IsNullOrWhiteSpace(openId)) throw new ArgumentException("OpenId is required.", nameof(openId));
        if (string.IsNullOrWhiteSpace(liveKey)) throw new ArgumentException("Live key is required.", nameof(liveKey));
        if (signingKey.Length < 32) throw new InvalidOperationException("The simulator signing key must be at least 256 bits.");
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var payload = new CredentialPayload(appId, openId, liveKey,
            now.ToUnixTimeSeconds(), now.Add(lifetime).ToUnixTimeSeconds(), nonce ?? Guid.NewGuid().ToString("N"));
        return Encode(payload);
    }

    public Task<VerifiedLaunch> VerifyLaunchAsync(PlatformLaunchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.AppId)) throw new PlatformLaunchRejectedException("malformed-credential", "AppId is required.");
        if (string.IsNullOrWhiteSpace(request.LaunchCredential)) throw new PlatformLaunchRejectedException("malformed-credential", "Launch credential is required.");
        if (string.IsNullOrWhiteSpace(request.LoginAttemptId)) throw new PlatformLaunchRejectedException("malformed-credential", "LoginAttemptId is required.");

        var parts = request.LaunchCredential.Split('.', 2);
        if (parts.Length != 2) throw new PlatformLaunchRejectedException("malformed-credential", "Launch credential is malformed.");
        byte[] payloadBytes, signature;
        try
        {
            payloadBytes = Base64Url.DecodeFromChars(parts[0]);
            signature = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            throw new PlatformLaunchRejectedException("malformed-credential", "Launch credential is not base64url.");
        }
        var expected = Sign(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
            throw new PlatformLaunchRejectedException("bad-signature", "Launch credential signature check failed.");

        CredentialPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<CredentialPayload>(payloadBytes)
                ?? throw new PlatformLaunchRejectedException("malformed-credential", "Launch credential payload is empty.");
        }
        catch (JsonException)
        {
            throw new PlatformLaunchRejectedException("malformed-credential", "Launch credential payload is unreadable.");
        }
        if (payload.Exp <= (clock ?? (() => DateTimeOffset.UtcNow))().ToUnixTimeSeconds())
            throw new PlatformLaunchRejectedException("credential-expired", "Launch credential has expired.");
        if (!StringComparer.Ordinal.Equals(payload.AppId, request.AppId))
            throw new PlatformLaunchRejectedException("app-mismatch", "Launch credential was issued for another AppId.");

        return Task.FromResult(new VerifiedLaunch(PlatformName, payload.AppId, payload.OpenId,
            payload.LiveKey, request.LoginAttemptId, (clock ?? (() => DateTimeOffset.UtcNow))()));
    }

    private byte[] Sign(byte[] payload) => HMACSHA256.HashData(signingKey, payload);

    private string Encode(CredentialPayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return $"{Base64Url.EncodeToString(payloadBytes)}.{Base64Url.EncodeToString(Sign(payloadBytes))}";
    }
}

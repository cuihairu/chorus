using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Chorus.Redesign.Rpc;

public sealed record BootstrapSeedSlot(string SlotId, string Role, string AssemblyPath, string Sha256Hex, int Port);
public sealed record BootstrapManifestPayload(
    string ClusterId, string NodeId, string RootCaPemPath, string NodeCaPemPath,
    string NodeAuthorityPfxPath, string ControlEndpoint, long ExpiresAtUnixMs,
    BootstrapSeedSlot[] SeedSlots);
public sealed record SignedBootstrapManifest(string PayloadBase64, string SignatureBase64);

public static class BootstrapManifest
{
    public static SignedBootstrapManifest Sign(BootstrapManifestPayload payload, X509Certificate2 root)
    {
        using var key = root.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("Offline root private key is required to sign bootstrap plans.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return new SignedBootstrapManifest(Convert.ToBase64String(bytes),
            Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256)));
    }

    public static BootstrapManifestPayload Verify(SignedBootstrapManifest signed, X509Certificate2 root)
    {
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Convert.FromBase64String(signed.PayloadBase64);
            signature = Convert.FromBase64String(signed.SignatureBase64);
        }
        catch (FormatException error) { throw new InvalidOperationException("Bootstrap manifest encoding is invalid.", error); }
        using var key = root.GetECDsaPublicKey()
            ?? throw new InvalidOperationException("Offline root public key is missing.");
        if (!key.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
            throw new InvalidOperationException("Bootstrap manifest signature is invalid.");
        var payload = JsonSerializer.Deserialize<BootstrapManifestPayload>(payloadBytes)
            ?? throw new InvalidOperationException("Bootstrap manifest payload is invalid.");
        if (payload.ExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ||
            string.IsNullOrWhiteSpace(payload.ClusterId) || string.IsNullOrWhiteSpace(payload.NodeId) ||
            payload.SeedSlots is null || payload.SeedSlots.Length == 0 ||
            payload.SeedSlots.Any(slot => slot.Role is not ("control-server" or "data-server") ||
                slot.SlotId != $"{payload.NodeId}/{slot.Role}" || slot.Port is < 1 or > 65535 ||
                string.IsNullOrWhiteSpace(slot.AssemblyPath) || string.IsNullOrWhiteSpace(slot.Sha256Hex)))
            throw new InvalidOperationException("Bootstrap manifest content is invalid or expired.");
        return payload;
    }
}

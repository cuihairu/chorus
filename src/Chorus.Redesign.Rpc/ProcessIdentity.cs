using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Chorus.Redesign.Rpc;

public sealed record ProcessIdentity(string ClusterId, string TenantId, string NodeId, string Role, string ProcessInstanceId);
public sealed record NodeCertificateGrant(string ClusterId, string NodeId, string[] Roles, string[] Tenants);

/// <summary>Validates an internal process certificate against the offline cluster root.</summary>
public static class ProcessCertificateValidator
{
    public const string NodeGrantOid = "1.3.6.1.4.1.55555.1.1";

    public static ProcessIdentity? Validate(
        X509Certificate2 certificate,
        X509Certificate2 root,
        string expectedClusterId,
        IEnumerable<X509Certificate2>? intermediates = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(root);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        if (intermediates is not null)
            foreach (var intermediate in intermediates) chain.ChainPolicy.ExtraStore.Add(intermediate);
        if (!chain.Build(certificate)) return null;
        if (chain.ChainElements.Count != 3) return null;
        var nodeCertificate = chain.ChainElements[1].Certificate;
        var grantExtension = nodeCertificate.Extensions[NodeGrantOid];
        if (grantExtension is null) return null;
        NodeCertificateGrant? grant;
        try { grant = JsonSerializer.Deserialize<NodeCertificateGrant>(grantExtension.RawData); }
        catch (JsonException) { return null; }
        if (grant is null || grant.Roles is null || grant.Tenants is null) return null;

        var san = certificate.Extensions["2.5.29.17"];
        if (san is null) return null;
        var reader = new AsnReader(san.RawData, AsnEncodingRules.DER).ReadSequence();
        ProcessIdentity? identity = null;
        while (reader.HasData)
        {
            var tag = reader.PeekTag();
            if (!tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
            {
                reader.ReadEncodedValue();
                continue;
            }

            var raw = reader.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6));
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "spiffe" || uri.Host != "chorus"
                || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                return null;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString).ToArray();
            if (segments.Length != 5 || segments.Any(s => string.IsNullOrWhiteSpace(s) || s.Contains('/'))) return null;
            var candidate = new ProcessIdentity(segments[0], segments[1], segments[2], segments[3], segments[4]);
            if (identity is not null) return null;
            identity = candidate;
        }

        return identity is { ClusterId: var cluster } &&
            StringComparer.Ordinal.Equals(cluster, expectedClusterId) &&
            StringComparer.Ordinal.Equals(grant.ClusterId, cluster) &&
            StringComparer.Ordinal.Equals(grant.NodeId, identity.NodeId) &&
            grant.Roles.Contains(identity.Role, StringComparer.Ordinal) &&
            grant.Tenants.Contains(identity.TenantId, StringComparer.Ordinal)
            ? identity : null;
    }
}

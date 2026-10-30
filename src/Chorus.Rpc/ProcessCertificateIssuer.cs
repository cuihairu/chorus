using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Chorus.Rpc;

/// <summary>Offline installation and node-local process certificate primitives.</summary>
public static class ProcessCertificateIssuer
{
    public static X509Certificate2 CreateRoot(string clusterId, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=chorus-root-{clusterId}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 1, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), notAfter);
    }

    public static X509Certificate2 CreateNodeAuthority(
        X509Certificate2 root, NodeCertificateGrant grant, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=chorus-node-{grant.NodeId}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509Extension(
            ProcessCertificateValidator.NodeGrantOid, JsonSerializer.SerializeToUtf8Bytes(grant), false));
        using var unsigned = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5), notAfter, RandomNumberGenerator.GetBytes(16));
        return unsigned.CopyWithPrivateKey(key);
    }

    public static X509Certificate2 CreateProcess(
        X509Certificate2 nodeAuthority, ProcessIdentity identity,
        DateTimeOffset notAfter, string? serverDnsName = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=chorus-{identity.Role}-{identity.ProcessInstanceId}",
            key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri($"spiffe://chorus/{Uri.EscapeDataString(identity.ClusterId)}/" +
            $"{Uri.EscapeDataString(identity.TenantId)}/{Uri.EscapeDataString(identity.NodeId)}/" +
            $"{Uri.EscapeDataString(identity.Role)}/{Uri.EscapeDataString(identity.ProcessInstanceId)}"));
        if (serverDnsName is not null) san.AddDnsName(serverDnsName);
        request.CertificateExtensions.Add(san.Build());
        using var unsigned = request.Create(nodeAuthority, DateTimeOffset.UtcNow.AddMinutes(-5),
            notAfter, RandomNumberGenerator.GetBytes(16));
        return unsigned.CopyWithPrivateKey(key);
    }
}

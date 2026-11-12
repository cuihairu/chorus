using System.Security.Cryptography.X509Certificates;
using Chorus.Rpc;

namespace Chorus.Engine.Tests;

/// <summary>
/// Issues process certificates the way a deployed cluster does: one cluster root, one node
/// authority granted the full role/tenant range, and per-process certificates issued beneath
/// it. Every issued certificate is remembered by the validator, so a handler's fence resolves
/// it — the same handshake the Kestrel listener performs in production, without a listener.
/// </summary>
internal sealed class TestProcessCertificates
{
    private static readonly string[] AllRoles =
    [
        "node-agent", "control-server", "data-server", "streamer-server",
        "game-server", "platform-gateway", "streamer-gateway"
    ];

    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _nodeAuthority;
    private readonly DateTimeOffset _notAfter = DateTimeOffset.UtcNow.AddDays(2);

    public TrustedProcessCertificates Trusted { get; } = new();

    public TestProcessCertificates()
    {
        _root = ProcessCertificateIssuer.CreateRoot("cluster-a", _notAfter);
        _nodeAuthority = ProcessCertificateIssuer.CreateNodeAuthority(_root,
            new NodeCertificateGrant("cluster-a", "node-a", AllRoles, ["@cluster", "tenant-a", "tenant-b"]),
            _notAfter);
    }

    /// <summary>Issues and registers a process certificate for one caller shape.</summary>
    public X509Certificate2 Issue(string tenant, string role, string instance)
    {
        var certificate = ProcessCertificateIssuer.CreateProcess(_nodeAuthority,
            new ProcessIdentity("cluster-a", tenant, "node-a", role, instance), _notAfter);
        Trusted.ValidateAndRemember(certificate, _root, "cluster-a", [_nodeAuthority]);
        return certificate;
    }

    /// <summary>A valid chain from a foreign cluster: one the validator never trusted.</summary>
    public X509Certificate2 ForeignClusterCertificate()
    {
        var root = ProcessCertificateIssuer.CreateRoot("cluster-b", _notAfter);
        var authority = ProcessCertificateIssuer.CreateNodeAuthority(root,
            new NodeCertificateGrant("cluster-b", "node-b", AllRoles, ["@cluster", "tenant-a"]),
            _notAfter);
        return ProcessCertificateIssuer.CreateProcess(authority,
            new ProcessIdentity("cluster-b", "tenant-a", "node-b", "streamer-server", "foreign"),
            _notAfter);
    }

    /// <summary>The call context a handler sees for this caller.</summary>
    public TestServerCallContext Context(X509Certificate2? certificate,
        CancellationToken cancellationToken = default) => new(certificate, cancellationToken);
}

using Chorus.Redesign.Rpc;

namespace Chorus.Engine.Tests;

public sealed class RedesignProcessCertificateTests
{
    [Fact]
    public void BootstrapManifest_RejectsTamperingAndExpiredPlan()
    {
        using var root = ProcessCertificateIssuer.CreateRoot("cluster-a", DateTimeOffset.UtcNow.AddDays(10));
        var payload = new BootstrapManifestPayload("cluster-a", "node-a", "root.pem", "node.pem", "node.pfx",
            "https://localhost:7500", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            [new("node-a/control-server", "control-server", "control.dll", new string('A', 64), 7500)]);
        var signed = BootstrapManifest.Sign(payload, root);

        Assert.Equal("node-a", BootstrapManifest.Verify(signed, root).NodeId);
        Assert.Throws<InvalidOperationException>(() => BootstrapManifest.Verify(
            signed with { SignatureBase64 = Convert.ToBase64String(new byte[64]) }, root));
        var expired = BootstrapManifest.Sign(payload with { ExpiresAtUnixMs = 1 }, root);
        Assert.Throws<InvalidOperationException>(() => BootstrapManifest.Verify(expired, root));
    }

    [Fact]
    public void NodeGrant_RejectsWrongRoleTenantNodeAndCluster()
    {
        using var root = ProcessCertificateIssuer.CreateRoot("cluster-a", DateTimeOffset.UtcNow.AddDays(10));
        var grant = new NodeCertificateGrant("cluster-a", "node-a", ["node-agent", "data-server"], ["@cluster", "tenant-a"]);
        using var node = ProcessCertificateIssuer.CreateNodeAuthority(root, grant, DateTimeOffset.UtcNow.AddDays(5));
        using var valid = ProcessCertificateIssuer.CreateProcess(node,
            new("cluster-a", "@cluster", "node-a", "data-server", "instance-a"), DateTimeOffset.UtcNow.AddDays(1));
        using var wrongRole = ProcessCertificateIssuer.CreateProcess(node,
            new("cluster-a", "@cluster", "node-a", "game-server", "instance-b"), DateTimeOffset.UtcNow.AddDays(1));
        using var wrongTenant = ProcessCertificateIssuer.CreateProcess(node,
            new("cluster-a", "tenant-b", "node-a", "data-server", "instance-c"), DateTimeOffset.UtcNow.AddDays(1));
        using var wrongNode = ProcessCertificateIssuer.CreateProcess(node,
            new("cluster-a", "@cluster", "node-b", "data-server", "instance-d"), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal("instance-a", ProcessCertificateValidator.Validate(valid, root, "cluster-a", [node])?.ProcessInstanceId);
        Assert.Null(ProcessCertificateValidator.Validate(wrongRole, root, "cluster-a", [node]));
        Assert.Null(ProcessCertificateValidator.Validate(wrongTenant, root, "cluster-a", [node]));
        Assert.Null(ProcessCertificateValidator.Validate(wrongNode, root, "cluster-a", [node]));
        Assert.Null(ProcessCertificateValidator.Validate(valid, root, "cluster-b", [node]));
    }
}

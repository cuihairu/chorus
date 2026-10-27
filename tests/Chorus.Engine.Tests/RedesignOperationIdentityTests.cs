using System.Text;
using Chorus.Protocol.Redesign;

namespace Chorus.Engine.Tests;

public sealed class RedesignOperationIdentityTests
{
    [Fact]
    public void ReceiptKey_IsScopedByTenantGameKindAndTarget()
    {
        var original = new OperationIdentity("tenant-a", "game-a", "grant", "player-a", "op-1");

        Assert.NotEqual(original, new OperationIdentity("tenant-b", "game-a", "grant", "player-a", "op-1"));
        Assert.NotEqual(original, new OperationIdentity("tenant-a", "game-b", "grant", "player-a", "op-1"));
        Assert.NotEqual(original, new OperationIdentity("tenant-a", "game-a", "spend", "player-a", "op-1"));
        Assert.NotEqual(original, new OperationIdentity("tenant-a", "game-a", "grant", "player-b", "op-1"));
        Assert.Equal(original, new OperationIdentity("tenant-a", "game-a", "grant", "player-a", "op-1"));
    }

    [Theory]
    [InlineData("", "game", "grant", "player", "op")]
    [InlineData("tenant", " ", "grant", "player", "op")]
    [InlineData("tenant", "game", " grant", "player", "op")]
    [InlineData("tenant", "game", "grant", "player", "")]
    public void ReceiptKey_RejectsAmbiguousIdentifiers(string tenant, string game, string kind, string target, string operation)
    {
        Assert.Throws<ArgumentException>(() => new OperationIdentity(tenant, game, kind, target, operation));
    }

    [Fact]
    public void Fingerprint_IsStableForSameCanonicalBytesAndChangesWithPayloadOrVersion()
    {
        var request = Encoding.UTF8.GetBytes("grant:player-a:100");
        var first = OperationFingerprint.FromCanonicalRequest(1, request);
        var retry = OperationFingerprint.FromCanonicalRequest(1, request);
        var changedPayload = OperationFingerprint.FromCanonicalRequest(1, Encoding.UTF8.GetBytes("grant:player-a:101"));
        var changedVersion = OperationFingerprint.FromCanonicalRequest(2, request);

        Assert.Equal(first, retry);
        Assert.NotEqual(first.Sha256Hex, changedPayload.Sha256Hex);
        Assert.NotEqual(first.Sha256Hex, changedVersion.Sha256Hex);
        Assert.Equal(64, first.Sha256Hex.Length);
    }

    [Fact]
    public void Fingerprint_RejectsUndefinedCanonicalVersion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationFingerprint.FromCanonicalRequest(0, []));
    }
}

using Chorus.EngineHost;

namespace Chorus.Engine.Tests;

/// <summary>
/// Broadcast-owner election for eng→gw snapshot dedup
/// (shared-memory-transport.md alt 1): the first client of a session owns
/// broadcast publishing; on owner disconnect duty transfers to a remaining
/// client, and the last disconnect clears ownership so a later re-connect
/// re-elects.
/// </summary>
public sealed class EngineBroadcastOwnerTests
{
    [Fact]
    public void FirstConnectOwns_SecondDoesNotSteal()
    {
        var owners = new BroadcastOwnerRegistry();
        owners.OnConnect("s1", "c1");
        owners.OnConnect("s1", "c2");

        Assert.True(owners.IsOwner("s1", "c1"));
        Assert.False(owners.IsOwner("s1", "c2"));
    }

    [Fact]
    public void OwnerDisconnect_TransfersToRemainingClient()
    {
        var owners = new BroadcastOwnerRegistry();
        owners.OnConnect("s1", "c1");
        owners.OnConnect("s1", "c2");

        owners.OnDisconnect("s1", "c1", remainingClientIds: new[] { "c2" });

        Assert.False(owners.IsOwner("s1", "c1"));
        Assert.True(owners.IsOwner("s1", "c2"));
    }

    [Fact]
    public void NonOwnerDisconnect_ChangesNothing()
    {
        var owners = new BroadcastOwnerRegistry();
        owners.OnConnect("s1", "c1");
        owners.OnConnect("s1", "c2");

        owners.OnDisconnect("s1", "c2", remainingClientIds: new[] { "c1" });

        Assert.True(owners.IsOwner("s1", "c1"));
    }

    [Fact]
    public void LastDisconnect_Clears_ReconnectReElects()
    {
        var owners = new BroadcastOwnerRegistry();
        owners.OnConnect("s1", "c1");

        owners.OnDisconnect("s1", "c1", remainingClientIds: Array.Empty<string>());
        Assert.False(owners.IsOwner("s1", "c1"));

        owners.OnConnect("s1", "c9");
        Assert.True(owners.IsOwner("s1", "c9"));
    }

    [Fact]
    public void SessionsAreIndependent()
    {
        var owners = new BroadcastOwnerRegistry();
        owners.OnConnect("s1", "c1");
        owners.OnConnect("s2", "c2");

        Assert.True(owners.IsOwner("s1", "c1"));
        Assert.True(owners.IsOwner("s2", "c2"));
        Assert.False(owners.IsOwner("s1", "c2"));
        Assert.False(owners.IsOwner("s2", "c1"));
    }
}

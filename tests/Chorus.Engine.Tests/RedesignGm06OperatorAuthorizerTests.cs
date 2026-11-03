using Chorus.ControlServer;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>The role and scope half of operator authorization (FIX-GM-06). The role ranks a
/// command's minimum requirement against the operator's role; the scope covers the target
/// tenant/game, with an empty array meaning all.</summary>
public sealed class RedesignGm06OperatorAuthorizerTests
{
    [Fact]
    public void AnOperatorWithAHighEnoughRoleIsAuthorized()
    {
        Assert.True(OperatorAuthorizer.HasRole("gm", "gm"));
        Assert.True(OperatorAuthorizer.HasRole("super", "gm"));
        Assert.True(OperatorAuthorizer.HasRole("operator", "viewer"));
    }

    [Fact]
    public void AnOperatorBelowTheRequiredRoleIsRefused()
    {
        Assert.False(OperatorAuthorizer.HasRole("viewer", "gm"));
        Assert.False(OperatorAuthorizer.HasRole("operator", "gm"));
        Assert.False(OperatorAuthorizer.HasRole("gm", "super"));
    }

    [Fact]
    public void AnUnknownRoleIsRefused()
    {
        Assert.False(OperatorAuthorizer.HasRole("anchor", "gm"));
        Assert.False(OperatorAuthorizer.HasRole("gm", "unknown"));
    }

    [Fact]
    public void AnEmptyScopeCoversEveryTenantAndGame()
    {
        var identity = new OperatorIdentity("gm-1", "gm", [], []);
        Assert.True(OperatorAuthorizer.InScope(identity, "tenant-a", "game-a"));
        Assert.True(OperatorAuthorizer.InScope(identity, "tenant-b", "game-z"));
    }

    [Fact]
    public void AListedTenantAndGameIsInScopeButAnUnlistedOneIsNot()
    {
        var identity = new OperatorIdentity("gm-1", "gm", ["tenant-a"], ["game-a"]);
        Assert.True(OperatorAuthorizer.InScope(identity, "tenant-a", "game-a"));
        // The other tenant is outside the operator's scope.
        Assert.False(OperatorAuthorizer.InScope(identity, "tenant-b", "game-a"));
        // The same tenant but a game the operator is not scoped to.
        Assert.False(OperatorAuthorizer.InScope(identity, "tenant-a", "game-b"));
    }

    [Fact]
    public void AGameOnlyScopeStillChecksTheTenant()
    {
        var identity = new OperatorIdentity("gm-1", "gm", [], ["game-a"]);
        Assert.True(OperatorAuthorizer.InScope(identity, "tenant-a", "game-a"));
        Assert.False(OperatorAuthorizer.InScope(identity, "tenant-a", "game-b"));
    }
}

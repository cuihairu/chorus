using Chorus.UnitySdk;
using Chorus.UnitySdk.Platforms.Douyin;

namespace Chorus.Engine.Tests;

/// <summary>
/// The Douyin platform adapter (FIX-U3D-05): the mount mapping the adapter owns is
/// pinned exactly — the streamer seat, the app id, the session context carried as the
/// platform produced it — and the bridge seam's failure behavior is pinned too: the
/// unavailable bridge refuses with the grant reason the admission matrix states, a
/// bridge failure propagates as the mount failure, and the mount honors cancellation.
/// The real LiveOpenSDK call sites are the bridge's (needs-permission rows); the
/// adapter tests hold the contract every future real bridge must satisfy.
/// </summary>
public sealed class RedesignU3D05DouyinAdapterTests
{
    [Fact]
    public async Task AMountMapsTheLaunchOntoTheUnifiedAnswer()
    {
        var bridge = new FakeBridge(new DouyinLaunch
        {
            StreamerOpenId = "open-douyin-1",
            RoomId = "room-7",
            LaunchCredentialJson = """{"token":"launch-credential"}""",
        });
        var adapter = new DouyinPlatformAdapter("tt-app-1", bridge);

        var mount = await adapter.MountAsync(CancellationToken.None);

        // The mount is the unified contract's answer, filled from the launch alone.
        Assert.Equal("douyin", mount.Platform);
        Assert.Equal("tt-app-1", mount.AppId);
        Assert.Equal("open-douyin-1", mount.StreamerId);
        Assert.Equal("streamer", mount.Role);
        Assert.Equal("""{"token":"launch-credential"}""", mount.SessionContextJson);
        // The room id is diagnosis material the bridge answers; the mount carries the
        // session context, not a Chorus room id — rooms are established server-side.
        Assert.Equal("room-7", bridge.LastLaunch!.RoomId);
    }

    [Fact]
    public async Task AMountNeverClaimsAPlayerSeat()
    {
        // Douyin's Unity gameplay is the streamer's client; viewers ride the platform's
        // server-side events, so the adapter's answer is fixed.
        var adapter = new DouyinPlatformAdapter("tt-app-1",
            new FakeBridge(new DouyinLaunch { StreamerOpenId = "open-1" }));
        var mount = await adapter.MountAsync(CancellationToken.None);
        Assert.Equal("streamer", mount.Role);
        Assert.Equal(string.Empty, mount.UserId);
    }

    [Fact]
    public async Task ARefusingBridgeSurfacesTheGrantReason()
    {
        // The only bridge that ships without credentials: the refusal names the exact
        // platform-side step, never a silent empty mount.
        var adapter = new DouyinPlatformAdapter("tt-app-1", new DouyinSdkUnavailableBridge());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.MountAsync(CancellationToken.None));
        Assert.Contains("LiveOpenSDK", failure.Message);
        Assert.Contains("Apply internal-test grant", failure.Message);
    }

    [Fact]
    public async Task ABridgeFailurePropagatesAsTheMountFailure()
    {
        var adapter = new DouyinPlatformAdapter("tt-app-1", new ThrowingBridge());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.MountAsync(CancellationToken.None));
        Assert.Contains("the sdk start failed", failure.Message);
    }

    [Fact]
    public async Task AMountHonorsCancellation()
    {
        var adapter = new DouyinPlatformAdapter("tt-app-1", new CancellingBridge());
        // TaskCanceledException derives from OperationCanceledException — either is the
        // mount honoring the cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.MountAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ShutdownReachesTheBridge()
    {
        var bridge = new FakeBridge(new DouyinLaunch());
        var adapter = new DouyinPlatformAdapter("tt-app-1", bridge);
        await adapter.ShutdownAsync();
        Assert.True(bridge.ShutdownCalled);
    }

    [Fact]
    public void AnEmptyAppIdIsRefusedAtConstruction() =>
        Assert.Throws<ArgumentException>(() => new DouyinPlatformAdapter("", new FakeBridge(new DouyinLaunch())));

    /// <summary>A bridge the tests control, remembering what the adapter asked of it.</summary>
    private sealed class FakeBridge : IDouyinSdkBridge
    {
        private readonly DouyinLaunch _launch;

        public FakeBridge(DouyinLaunch launch) => _launch = launch;

        public DouyinLaunch? LastLaunch { get; private set; }
        public bool ShutdownCalled { get; private set; }

        public Task<DouyinLaunch> StartAsync(CancellationToken cancellationToken)
        {
            LastLaunch = _launch;
            return Task.FromResult(_launch);
        }

        public Task ShutdownAsync()
        {
            ShutdownCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingBridge : IDouyinSdkBridge
    {
        public Task<DouyinLaunch> StartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the sdk start failed");

        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private sealed class CancellingBridge : IDouyinSdkBridge
    {
        public async Task<DouyinLaunch> StartAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new DouyinLaunch();
        }

        public Task ShutdownAsync() => Task.CompletedTask;
    }
}

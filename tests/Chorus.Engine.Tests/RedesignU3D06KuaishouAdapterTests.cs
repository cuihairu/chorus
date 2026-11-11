using Chorus.UnitySdk;
using Chorus.UnitySdk.Platforms.Kuaishou;

namespace Chorus.Engine.Tests;

/// <summary>
/// The Kuaishou platform adapter (FIX-U3D-06), the mirror of the Douyin one: the
/// mount mapping the adapter owns is pinned exactly — the streamer seat, the room
/// code standing in as the streamer handle the server resolves through bind, the
/// session context carried as the platform produced it — and the bridge seam's
/// failure behavior is pinned too. The real companion-IPC call sites are the
/// bridge's (needs-permission rows); the adapter tests hold the contract every
/// future real bridge must satisfy.
/// </summary>
public sealed class RedesignU3D06KuaishouAdapterTests
{
    [Fact]
    public async Task AMountMapsTheLaunchOntoTheUnifiedAnswer()
    {
        var bridge = new FakeBridge(new KuaishouLaunch
        {
            RoomCode = "rc-9001",
            IpcChannelName = "ks-ipc-7",
            LaunchCredentialJson = """{"roomCode":"rc-9001","ipc":"ks-ipc-7"}""",
        });
        var adapter = new KuaishouPlatformAdapter("ks-app-1", bridge);

        var mount = await adapter.MountAsync(CancellationToken.None);

        // The mount is the unified contract's answer, filled from the launch alone.
        Assert.Equal("kuaishou", mount.Platform);
        Assert.Equal("ks-app-1", mount.AppId);
        Assert.Equal("rc-9001", mount.StreamerId);
        Assert.Equal("streamer", mount.Role);
        Assert.Equal("""{"roomCode":"rc-9001","ipc":"ks-ipc-7"}""", mount.SessionContextJson);
        // The ipc channel is diagnosis material the bridge answers; the mount carries
        // the session context, not a companion channel name.
        Assert.Equal("ks-ipc-7", bridge.LastLaunch!.IpcChannelName);
    }

    [Fact]
    public async Task AMountCarriesTheRoomCodeAsTheStreamerHandle()
    {
        // Kuaishou hands no client-side streamer open id: the room code is the
        // platform-side handle the server resolves through its bind call — pinned so
        // a future real bridge cannot quietly change the meaning of StreamerId.
        var adapter = new KuaishouPlatformAdapter("ks-app-1",
            new FakeBridge(new KuaishouLaunch { RoomCode = "rc-42" }));
        var mount = await adapter.MountAsync(CancellationToken.None);
        Assert.Equal("rc-42", mount.StreamerId);
    }

    [Fact]
    public async Task AMountNeverClaimsAPlayerSeat()
    {
        var adapter = new KuaishouPlatformAdapter("ks-app-1",
            new FakeBridge(new KuaishouLaunch { RoomCode = "rc-42" }));
        var mount = await adapter.MountAsync(CancellationToken.None);
        Assert.Equal("streamer", mount.Role);
        Assert.Equal(string.Empty, mount.UserId);
    }

    [Fact]
    public async Task ARefusingBridgeSurfacesTheGrantReason()
    {
        // The only bridge that ships without credentials: the refusal names the exact
        // platform-side step, never a silent empty mount.
        var adapter = new KuaishouPlatformAdapter("ks-app-1", new KuaishouSdkUnavailableBridge());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.MountAsync(CancellationToken.None));
        Assert.Contains("companion IPC", failure.Message);
        Assert.Contains("scope grant", failure.Message);
    }

    [Fact]
    public async Task ABridgeFailurePropagatesAsTheMountFailure()
    {
        var adapter = new KuaishouPlatformAdapter("ks-app-1", new ThrowingBridge());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.MountAsync(CancellationToken.None));
        Assert.Contains("the ipc handshake failed", failure.Message);
    }

    [Fact]
    public async Task AMountHonorsCancellation()
    {
        var adapter = new KuaishouPlatformAdapter("ks-app-1", new CancellingBridge());
        // TaskCanceledException derives from OperationCanceledException — either is the
        // mount honoring the cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.MountAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ShutdownReachesTheBridge()
    {
        var bridge = new FakeBridge(new KuaishouLaunch());
        var adapter = new KuaishouPlatformAdapter("ks-app-1", bridge);
        await adapter.ShutdownAsync();
        Assert.True(bridge.ShutdownCalled);
    }

    [Fact]
    public void AnEmptyAppIdIsRefusedAtConstruction() =>
        Assert.Throws<ArgumentException>(() => new KuaishouPlatformAdapter("", new FakeBridge(new KuaishouLaunch())));

    /// <summary>A bridge the tests control, remembering what the adapter asked of it.</summary>
    private sealed class FakeBridge : IKuaishouSdkBridge
    {
        private readonly KuaishouLaunch _launch;

        public FakeBridge(KuaishouLaunch launch) => _launch = launch;

        public KuaishouLaunch? LastLaunch { get; private set; }
        public bool ShutdownCalled { get; private set; }

        public Task<KuaishouLaunch> StartAsync(CancellationToken cancellationToken)
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

    private sealed class ThrowingBridge : IKuaishouSdkBridge
    {
        public Task<KuaishouLaunch> StartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the ipc handshake failed");

        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private sealed class CancellingBridge : IKuaishouSdkBridge
    {
        public async Task<KuaishouLaunch> StartAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new KuaishouLaunch();
        }

        public Task ShutdownAsync() => Task.CompletedTask;
    }
}

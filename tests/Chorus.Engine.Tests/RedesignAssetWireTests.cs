using Chorus.Rpc;
using Chorus.StreamerServer;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-LINK-07: the StreamerServer's asset channel is a real m0 DataService ChangeAsset client
/// (the m0 contract names StreamerServer as the client). Concurrent changes serialize on the
/// account row, the non-negative balance constraint surfaces as the SS's domain conflict, and an
/// operation-id replay returns the stored receipt without re-running the effect (AT18 并发资产增减:
/// 余额/库存和流水一致). The trigger (GM 允许的资产调整) is FIX-GM-07; this proves the arrow.
/// </summary>
public sealed class RedesignAssetWireTests
{
    [Fact]
    public async Task TheStreamerAssetChannelChangesBalancesThroughTheM0Surface()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var streamer = environment.Certificate("tenant-a", "streamer-server", "streamer-1");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        // The StreamerServer's own asset channel: the arrow under acceptance. The channel creates
        // and disposes a channel per call (the production DataChannel factory pattern).
        var assets = new StreamerAssetChannel(() => environment.Channel(environment.DataPort, streamer, "data-server"));
        // An independent m0 client for read-back.
        using var dataChannel = environment.Channel(environment.DataPort, streamer, "data-server");
        var data = new DataService.DataServiceClient(dataChannel);

        // (a) a first change seeds the account row (no row before) and lands.
        var first = await assets.ChangeAsync("tenant-a", "game-a", "streamer-1", "grant-1",
            "coins", delta: 100, "test");
        Assert.False(first.Duplicate);
        Assert.Equal(100, first.Balance);

        // (b) replaying the same operation id returns the stored receipt (Duplicate) without
        // re-running the effect: the balance does not double.
        var replay = await assets.ChangeAsync("tenant-a", "game-a", "streamer-1", "grant-1",
            "coins", delta: 100, "test");
        Assert.True(replay.Duplicate);
        Assert.Equal(100, replay.Balance);

        // (c) a change that would drive the balance negative is the explicit constraint conflict;
        // its transaction (receipt included) rolls back, so the operation id stays unused.
        await Assert.ThrowsAsync<StreamerAssetConflictException>(
            () => assets.ChangeAsync("tenant-a", "game-a", "streamer-1", "grant-2",
                "coins", delta: -200, "test"));

        // (d) concurrent changes on the same account row serialize: both land. Their per-call
        // balances depend on lock order, so assert the commit, not each call's view.
        var landed = await Task.WhenAll(
            assets.ChangeAsync("tenant-a", "game-a", "streamer-1", "grant-3", "coins", delta: 30, "test"),
            assets.ChangeAsync("tenant-a", "game-a", "streamer-1", "grant-4", "coins", delta: 20, "test"));
        Assert.All(landed, change => Assert.False(change.Duplicate));

        // (e) read-back through the m0 surface: 100 + 30 + 20 = 150, and the row version proves
        // the serialization (seed version 1 + three committed updates; the failed grant-2 and the
        // replayed grant-1 left no version trace).
        var asset = await data.GetAssetAsync(new AssetRequest
        {
            TenantId = "tenant-a", GameId = "game-a", PlayerId = "streamer-1", AssetId = "coins"
        });
        Assert.Equal(150, asset.Balance);
        Assert.Equal(4, asset.Version);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Chorus.Protocol;
using Chorus.Rpc;
using Chorus.StreamerServer;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-LINK-06: the StreamerServer's profile channel is a real m0 DataService Get/PatchProfile
/// client (the m0 contract names StreamerServer as the client). The DS-side CAS conflict surfaces
/// as the SS's domain conflict (AT18), and a rewards-shaped profile write never touches the asset
/// balance (无 JSON 覆盖奖励). The trigger (GM 主播资料修正) is FIX-GM-07; this proves the arrow.
/// </summary>
public sealed class RedesignProfileWireTests
{
    [Fact]
    public async Task TheStreamerProfileChannelReadsAndWritesThroughTheM0Surface()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var streamer = environment.Certificate("tenant-a", "streamer-server", "streamer-1");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        // The StreamerServer's own profile channel: the arrow under acceptance. The channel creates
        // and disposes a channel per call (the production DataChannel factory pattern).
        var profile = new StreamerProfileChannel(() => environment.Channel(environment.DataPort, streamer, "data-server"));
        // An independent m0 client for read-back and reward-state assertions.
        using var dataChannel = environment.Channel(environment.DataPort, streamer, "data-server");
        var data = new DataService.DataServiceClient(dataChannel);

        // (a) a fresh row is not found.
        Assert.Null(await profile.LoadAsync("tenant-a", "game-a", "streamer-1"));

        // (b) a version-0 patch inserts the row (version 1); the m0 surface reads it back.
        var first = await profile.PatchAsync("tenant-a", "game-a", "streamer-1", "op-1",
            expectedVersion: 0, schemaVersion: 1, dataJson: """{"name":"a"}""");
        Assert.Equal(1, first.Version);
        var read = await data.GetProfileAsync(new ProfileRequest
        {
            TenantId = "tenant-a", GameId = "game-a", PlayerId = "streamer-1"
        });
        Assert.True(read.Found);
        Assert.Equal(1, read.Version);
        // player.profile.data is jsonb: the read-back text is PostgreSQL-normalized (spacing),
        // so compare the JSON semantically, not by exact string.
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(read.DataJson), JsonNode.Parse("""{"name":"a"}""")));

        // (c) a stale version-0 patch is an explicit CAS conflict (AT18).
        await Assert.ThrowsAsync<StreamerProfileConflictException>(
            () => profile.PatchAsync("tenant-a", "game-a", "streamer-1", "op-2",
                expectedVersion: 0, schemaVersion: 1, dataJson: """{"name":"b"}"""));

        // (d) a rewards-shaped profile write never touches the asset balance (无 JSON 覆盖奖励).
        await data.ChangeAssetAsync(new ChangeAssetRequest
        {
            Key = NewKey("tenant-a", "game-a", "asset.change", "streamer-1", "grant-1",
                new { AssetId = "points", Delta = 100L, Reason = "test" }),
            AssetId = "points", Delta = 100, Reason = "test"
        });
        var patched = await profile.PatchAsync("tenant-a", "game-a", "streamer-1", "op-3",
            expectedVersion: 1, schemaVersion: 1, dataJson: """{"assets":{"points":999999}}""");
        Assert.Equal(2, patched.Version);
        var asset = await data.GetAssetAsync(new AssetRequest
        {
            TenantId = "tenant-a", GameId = "game-a", PlayerId = "streamer-1", AssetId = "points"
        });
        // The profile JSON claims 999999 points; the reward state is a separate table and is intact.
        Assert.Equal(100, asset.Balance);
    }

    private static OperationKey NewKey(string tenant, string game, string kind, string target,
        string operation, object canonical)
    {
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1,
            JsonSerializer.SerializeToUtf8Bytes(canonical));
        return new OperationKey
        {
            TenantId = tenant, GameId = game, OperationKind = kind,
            TargetId = target, OperationId = operation, CanonicalVersion = 1,
            RequestHashHex = fingerprint.Sha256Hex
        };
    }
}

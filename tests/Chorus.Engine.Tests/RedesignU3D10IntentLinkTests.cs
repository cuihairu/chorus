using System;
using System.Linq;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Chorus.UnitySdk;
using Chorus.UnitySdk.Edge;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The intent send (FIX-U3D-10): one game intent rides the wire's gameCommand into its
/// room's inbox and the accepted word comes back — through the stream's pumping, since
/// the stream owns the downlink and resolves the waiting send. The real link runs end
/// to end against a real match cluster: a boost reaches the room's module (the consuming
/// delta carries its score effect), and a refused intent answers its own send visibly
/// while the stream keeps reading.
/// </summary>
public sealed class RedesignU3D10IntentLinkTests
{
    [Fact]
    public async Task AnIntentReachesItsRoomModuleAndTheAcceptedWordComesBack()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);

        var first = await LoginAndStartAsync(wssPort, simulator, "open-u3d10-a", certs);
        var second = await LoginAndStartAsync(wssPort, simulator, "open-u3d10-b", certs);
        await first.JoinMatchAsync("standard", null, CancellationToken.None);
        await second.JoinMatchAsync("standard", null, CancellationToken.None);
        var room = await first.AwaitRoomAsync(CancellationToken.None);
        await second.AwaitRoomAsync(CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var stream = await first.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);
        var applier = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);

        // The game pumps the stream from its own loop; every answer — receipts included —
        // arrives through that pumping.
        var sawScoreEffect = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = PumpAsync(stream, applier, sawScoreEffect, budget.Token);

        // The module ignores client payloads carrying a "kind" key (FIX-SYNC-09 trust
        // boundary): the boost names its action, not a platform event shape.
        var reply = await first.SendIntentAsync(new ClientIntent
        {
            RoomId = room.RoomId,
            RoomEpoch = room.RoomEpoch,
            CommandId = "cmd-u3d10-1",
            PayloadJson = """{"action":"boost","level":3}""",
        }, budget.Token);

        // The accepted word: this command, this room, one durable sequence.
        Assert.Equal("cmd-u3d10-1", reply.CommandId);
        Assert.Equal(room.RoomId, reply.RoomId);
        Assert.True(reply.Sequence > 0);
        Assert.False(reply.Duplicate);

        // The proof of "reaches the module" is the score effect in the committed delta —
        // the boost's faction entry, not merely an accepted receipt.
        var effect = await sawScoreEffect.Task.WaitAsync(budget.Token);
        Assert.Contains("\"faction\":\"red\"", effect);

        await first.DisconnectAsync();
        await second.DisconnectAsync();
        await SwallowAsync(pump);
    }

    [Fact]
    public async Task ARefusedIntentAnswersItsOwnSendAndTheStreamSurvives()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);

        var first = await LoginAndStartAsync(wssPort, simulator, "open-u3d10-c", certs);
        var second = await LoginAndStartAsync(wssPort, simulator, "open-u3d10-d", certs);
        await first.JoinMatchAsync("standard", null, CancellationToken.None);
        await second.JoinMatchAsync("standard", null, CancellationToken.None);
        var room = await first.AwaitRoomAsync(CancellationToken.None);
        await second.AwaitRoomAsync(CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var stream = await first.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);
        var applier = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);
        var pump = PumpAsync(stream, applier, null, budget.Token);

        // The upstream fence refuses a guessed placement at the gateway, naming the
        // command id on the refusal — the send hears its own answer.
        var refusal = await Assert.ThrowsAsync<EdgeProtocolException>(() => first.SendIntentAsync(
            new ClientIntent
            {
                RoomId = "room-not-mine", RoomEpoch = room.RoomEpoch,
                CommandId = "cmd-u3d10-other", PayloadJson = """{"action":"boost"}""",
            }, budget.Token));
        Assert.Equal("invalid_message", refusal.Code);

        // The refusal is per-command: the connection and the stream keep reading, and
        // this very intent's own receipt resolves through the surviving pump.
        var reply = await first.SendIntentAsync(new ClientIntent
        {
            RoomId = room.RoomId, RoomEpoch = room.RoomEpoch,
            CommandId = "cmd-u3d10-own", PayloadJson = """{"action":"boost","level":1}""",
        }, budget.Token);
        Assert.Equal("cmd-u3d10-own", reply.CommandId);
        Assert.True(reply.Sequence > 0);

        await first.DisconnectAsync();
        await second.DisconnectAsync();
        await SwallowAsync(pump);
    }

    /// <summary>The game's pump loop: advance the stream, decide every frame, and name
    /// the first score effect a committed delta carries. A null watcher just pumps.</summary>
    private static async Task PumpAsync(ISyncStream stream, SyncApplier applier,
        TaskCompletionSource<string>? watcher, CancellationToken cancellationToken)
    {
        try
        {
            while (await stream.MoveNextAsync(cancellationToken))
            {
                var decision = applier.Decide(stream.Current);
                if (decision.Kind != SyncDecisionKind.ApplyDelta || watcher is null) continue;
                var effect = stream.Current.Delta!.Effects.FirstOrDefault(entry =>
                    entry.PayloadJson.Contains("\"faction\":\"red\"", StringComparison.Ordinal));
                if (effect is not null) watcher.TrySetResult(effect.PayloadJson);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static async Task SwallowAsync(Task pump)
    {
        try
        {
            await pump;
        }
        catch
        {
            // The disconnect ends the pump's read mid-wait; everything after the facts
            // held is teardown.
        }
    }

    private static async Task<ChorusSession> LoginAndStartAsync(int wssPort,
        Chorus.Platform.DevelopmentPlatformSimulator simulator, string openId,
        RemoteCertificateValidationCallback certs)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(5));
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", openId, credential);
        var sdk = new ChorusSdk(configuration => new EdgeChorusTransport(configuration, certs));
        var configuration = RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, wssPort);
        return await sdk.ConnectAsync(configuration, adapter, CancellationToken.None);
    }

    private static RemoteCertificateValidationCallback Trusts(string rootPem, string nodeAuthorityPem)
    {
        // The certs are NOT disposed: the closure must keep them alive for every
        // handshake this transport runs.
        var root = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(rootPem);
        var nodeAuthority = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            nodeAuthorityPem);
        return (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var candidate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
            using var validator = new System.Security.Cryptography.X509Certificates.X509Chain();
            validator.ChainPolicy.TrustMode =
                System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
            validator.ChainPolicy.RevocationMode =
                System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
            validator.ChainPolicy.CustomTrustStore.Add(root);
            validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
            return validator.Build(candidate);
        };
    }
}

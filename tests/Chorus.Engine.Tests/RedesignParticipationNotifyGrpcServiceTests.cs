using Chorus.Rpc;
using Chorus.StreamerServer;
using Grpc.Core;

namespace Chorus.Engine.Tests;

/// <summary>
/// The participation-commit face driven as plain method calls: the control plane's dispatcher
/// pushes a mount's round commit and the notice either lands in the mounted actor's binding
/// facts or is absorbed idempotently. The certificate fence runs against issued process
/// certificates — the dispatcher's cluster identity and a tenant-scoped control process.
/// </summary>
public sealed class RedesignParticipationNotifyGrpcServiceTests
{
    private sealed record Rig(
        ParticipationNotifyGrpcService Service, TestProcessCertificates Certificates,
        StreamerActorRegistry Registry)
    {
        public TestServerCallContext ControlContext() =>
            Certificates.Context(Certificates.Issue("@cluster", "control-server", "dispatcher"));
    }

    private static Rig New(StreamerBinding? binding = null)
    {
        var certificates = new TestProcessCertificates();
        var registry = new StreamerActorRegistry();
        var service = new ParticipationNotifyGrpcService(registry, certificates.Trusted);
        if (binding is not null)
            registry.GetOrAdd("tenant-a", "streamer-fake").OnGameplayBindingAsync(binding).Wait();
        return new Rig(service, certificates, registry);
    }

    private static NotifyCommittedRequest Notice(
        string roomId = "room-x", long roundGeneration = 5,
        string bindingId = "binding-fake", long bindingGeneration = 1,
        string streamerId = "streamer-fake", string tenantId = "tenant-a") => new()
    {
        TenantId = tenantId, GameId = "game-a", RoomId = roomId, RoundGeneration = roundGeneration,
        BindingId = bindingId, BindingGeneration = bindingGeneration, StreamerId = streamerId
    };

    private static StreamerBinding MountedBinding() =>
        new("binding-fake", "live-fake", "game-a", 1, "Ready", false);

    private static async Task Rejects(Func<Task> call, StatusCode code, string detail)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, error.Status.StatusCode);
        Assert.Equal(detail, error.Status.Detail);
    }

    [Fact]
    public async Task TheCommitNoticeFillsTheMountedRoomIntoTheBindingFacts()
    {
        var rig = New(MountedBinding());
        var actor = rig.Registry.Find("tenant-a", "streamer-fake")!;
        var reply = await rig.Service.NotifyCommitted(Notice(), rig.ControlContext());
        Assert.True(reply.Applied);
        Assert.Equal("room-x", actor.Facts!.ActiveBinding!.CurrentRoomId);
        Assert.Equal(5, actor.Facts.ActiveBinding.RoundGeneration);
    }

    [Fact]
    public async Task ARepeatNoticeAtTheSameGenerationDoesNotRegressTheFacts()
    {
        var rig = New(MountedBinding());
        var actor = rig.Registry.Find("tenant-a", "streamer-fake")!;
        await rig.Service.NotifyCommitted(Notice(), rig.ControlContext());
        var repeat = await rig.Service.NotifyCommitted(Notice(roomId: "room-stale"), rig.ControlContext());
        Assert.True(repeat.Applied); // idempotent: a converged notice is a no-op, not an error
        Assert.Equal("room-x", actor.Facts!.ActiveBinding!.CurrentRoomId);
        Assert.Equal(5, actor.Facts.ActiveBinding.RoundGeneration);
    }

    [Fact]
    public async Task ANoticeForAMovedBindingIsIgnored()
    {
        var rig = New(MountedBinding());
        var actor = rig.Registry.Find("tenant-a", "streamer-fake")!;
        var reply = await rig.Service.NotifyCommitted(Notice(bindingId: "binding-other"), rig.ControlContext());
        Assert.True(reply.Applied);
        Assert.Null(actor.Facts!.ActiveBinding!.CurrentRoomId);
        Assert.Equal(0, actor.Facts.ActiveBinding.RoundGeneration);
    }

    [Fact]
    public async Task ANoticeForAnAbsentActorIsANoOpButStillApplied()
    {
        var rig = New();
        var reply = await rig.Service.NotifyCommitted(Notice(), rig.ControlContext());
        Assert.True(reply.Applied); // the dispatcher marks the outbox done; recovery covers the rest
        Assert.Null(rig.Registry.Find("tenant-a", "streamer-fake"));
    }

    [Fact]
    public async Task ANoticeFromATenantScopedCallerIsFencedToItsTenant()
    {
        var rig = New(MountedBinding());
        var certificate = rig.Certificates.Issue("tenant-a", "control-server", "control-1");
        await Rejects(() => rig.Service.NotifyCommitted(Notice(tenantId: "tenant-b"),
                rig.Certificates.Context(certificate)),
            StatusCode.PermissionDenied, "Tenant is not authorized.");
    }

    [Fact]
    public async Task AControlServerCallerMayNameItsOwnTenant()
    {
        var rig = New(MountedBinding());
        var certificate = rig.Certificates.Issue("tenant-a", "control-server", "control-1");
        var reply = await rig.Service.NotifyCommitted(Notice(), rig.Certificates.Context(certificate));
        Assert.True(reply.Applied);
        Assert.Equal("room-x",
            rig.Registry.Find("tenant-a", "streamer-fake")!.Facts!.ActiveBinding!.CurrentRoomId);
    }

    [Fact]
    public async Task AnIncompleteNoticeIsInvalid()
    {
        var rig = New(MountedBinding());
        await Rejects(() => rig.Service.NotifyCommitted(Notice(roomId: ""), rig.ControlContext()),
            StatusCode.InvalidArgument, "The participation notice is incomplete.");
    }

    [Fact]
    public async Task ANonPositiveRoundGenerationIsInvalid()
    {
        var rig = New(MountedBinding());
        await Rejects(() => rig.Service.NotifyCommitted(Notice(roundGeneration: 0), rig.ControlContext()),
            StatusCode.InvalidArgument, "The round generation must be a positive fact.");
    }

    [Fact]
    public async Task ACallerOutsideTheControlRoleIsRefused()
    {
        var rig = New(MountedBinding());
        var certificate = rig.Certificates.Issue("tenant-a", "streamer-server", "svc");
        await Rejects(() => rig.Service.NotifyCommitted(Notice(), rig.Certificates.Context(certificate)),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task ACertificateFromAForeignClusterIsUnauthenticated()
    {
        var rig = New(MountedBinding());
        await Rejects(() => rig.Service.NotifyCommitted(Notice(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task AnAbsentCertificateIsUnauthenticated()
    {
        var rig = New(MountedBinding());
        await Rejects(() => rig.Service.NotifyCommitted(Notice(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }
}

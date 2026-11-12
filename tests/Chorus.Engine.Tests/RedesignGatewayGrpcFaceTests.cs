using Chorus.PlatformGateway;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.Engine.Tests;

/// <summary>
/// The platform gateway's face driven as plain method calls: readiness for the supervisors
/// that poll it, and the stop-delivery command that installs a room's closure. The command is
/// the control plane's word alone — the gateway accepts no peer's — and it is idempotent only
/// at the version it recorded, so a late replay can never close a newer round. The record is
/// process-wide and in-memory, which is exactly why the duplicate and the stale-version
/// refusals are the observable contract here.
/// </summary>
public sealed class RedesignGatewayGrpcFaceTests
{
    [Fact]
    public async Task ReadyAnswersForTheSupervisors()
    {
        var rig = GatewayRig.Create();
        var reply = await rig.Service.Ready(new Empty(), rig.Context("node-agent", "node-1"));
        Assert.True(reply.Ready);
        Assert.Equal("4", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        var rig = GatewayRig.Create();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task ReadyRefusesAForeignCertificate()
    {
        var rig = GatewayRig.Create();
        await rig.Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheGatewayPlane()
    {
        var rig = GatewayRig.Create();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Context("streamer-gateway", "gw-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task StopDeliveryInstallsTheRoomsClosure()
    {
        var rig = GatewayRig.Create();
        var reply = await rig.Service.StopDelivery(Command(), rig.Context("control-server", "control-1"));
        Assert.Equal("op-end-1", reply.EndOperationId);
        Assert.Equal(41, reply.CloseRouteVersion);
        Assert.False(reply.Duplicate);
        // The closure is installed process-wide: the gateway's own gate answers closed, and
        // the drain queue carries the room for dispose and ack.
        Assert.True(rig.Record.IsClosed("room-1", 7));
        Assert.True(rig.Record.DrainQueue.TryRead(out _));
    }

    [Fact]
    public async Task StopDeliveryAnswersDuplicateAtTheRecordedVersion()
    {
        var rig = GatewayRig.Create();
        var control = rig.Context("control-server", "control-1");
        await rig.Service.StopDelivery(Command(), control);
        var reply = await rig.Service.StopDelivery(Command(), control);
        Assert.True(reply.Duplicate);
        Assert.Equal(41, reply.CloseRouteVersion);
    }

    [Fact]
    public async Task StopDeliveryRefusesACommandNamingANewerVersion()
    {
        var rig = GatewayRig.Create();
        var control = rig.Context("control-server", "control-1");
        await rig.Service.StopDelivery(Command(), control);
        // A later round replays the same room id with a different version: closing it would
        // close a newer round, so the recorded version is the one that refuses.
        await rig.Rejects(() => rig.Service.StopDelivery(Command(closeRouteVersion: 42), control),
            StatusCode.FailedPrecondition,
            "The room room-1 already closed its delivery at version 41; the command names 42.");
    }

    [Fact]
    public async Task StopDeliveryRefusesAnIncompleteCommand()
    {
        var rig = GatewayRig.Create();
        var control = rig.Context("control-server", "control-1");
        // Every field is a fact the drain pass and the ack rely on: a command missing any of
        // them is incomplete, not guessable.
        await rig.Rejects(() => rig.Service.StopDelivery(Command(tenantId: ""), control),
            StatusCode.InvalidArgument, "The stop-delivery command is incomplete.");
        await rig.Rejects(() => rig.Service.StopDelivery(Command(gameId: "   "), control),
            StatusCode.InvalidArgument, "The stop-delivery command is incomplete.");
        await rig.Rejects(() => rig.Service.StopDelivery(Command(roomId: ""), control),
            StatusCode.InvalidArgument, "The stop-delivery command is incomplete.");
        await rig.Rejects(() => rig.Service.StopDelivery(Command(endOperationId: ""), control),
            StatusCode.InvalidArgument, "The stop-delivery command is incomplete.");
    }

    [Fact]
    public async Task StopDeliveryRefusesACommandWithoutItsVersions()
    {
        var rig = GatewayRig.Create();
        var control = rig.Context("control-server", "control-1");
        await rig.Rejects(() => rig.Service.StopDelivery(Command(roundGeneration: 0), control),
            StatusCode.InvalidArgument,
            "The stop-delivery command must name its round generation and close version.");
        await rig.Rejects(() => rig.Service.StopDelivery(Command(closeRouteVersion: 0), control),
            StatusCode.InvalidArgument,
            "The stop-delivery command must name its round generation and close version.");
    }

    [Fact]
    public async Task StopDeliveryRefusesACallerThatIsNotTheControlPlane()
    {
        var rig = GatewayRig.Create();
        // A data server polls readiness but issues no closure: only the control plane records
        // a round's end.
        await rig.Rejects(() => rig.Service.StopDelivery(Command(), rig.Context("data-server", "data-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task ARefusedCommandInstallsNoClosure()
    {
        var rig = GatewayRig.Create();
        await rig.Rejects(() => rig.Service.StopDelivery(Command(roomId: ""),
            rig.Context("control-server", "control-1")),
            StatusCode.InvalidArgument, "The stop-delivery command is incomplete.");
        Assert.False(rig.Record.IsClosed("room-1", 7));
        Assert.False(rig.Record.DrainQueue.TryRead(out _));
    }

    private static StopDeliveryRequest Command(
        string tenantId = "tenant-a", string gameId = "game-a", string roomId = "room-1",
        string endOperationId = "op-end-1", long roundGeneration = 7, long closeRouteVersion = 41) => new()
        {
            TenantId = tenantId, GameId = gameId, RoomId = roomId,
            EndOperationId = endOperationId, RoundGeneration = roundGeneration,
            CloseRouteVersion = closeRouteVersion
        };

    /// <summary>A gateway face and the process-wide closure record it installs into. The
    /// record is built once and handed to both the service and the rig: it is process-wide
    /// truth, so a rig holding its own copy would assert on a record the service never wrote.</summary>
    private sealed record GatewayRig(GatewayGrpcService Service, StopDeliveryRecord Record,
        TestProcessCertificates Certificates)
    {
        public static GatewayRig Create()
        {
            var certificates = new TestProcessCertificates();
            var record = new StopDeliveryRecord();
            return new GatewayRig(new GatewayGrpcService(certificates.Trusted, record), record, certificates);
        }

        public TestServerCallContext Context(string role, string instance) =>
            Certificates.Context(Certificates.Issue("@cluster", role, instance));

        public async Task Rejects(Func<Task> call, StatusCode code, string detail)
        {
            var error = await Assert.ThrowsAsync<RpcException>(call);
            Assert.Equal(code, error.Status.StatusCode);
            Assert.Equal(detail, error.Status.Detail);
        }
    }
}

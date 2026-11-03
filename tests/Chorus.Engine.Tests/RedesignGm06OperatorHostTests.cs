using System.Security.Cryptography.X509Certificates;
using Chorus.ControlServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The operator-authenticated GM entry (FIX-GM-06, architecture 12.1). The operator identity
/// comes from the bearer credential in the request metadata, never the request body and never
/// the process certificate: an anchor identity, a process certificate, a low role, a scope
/// that does not cover the target, a forged request operator, a disabled operator and an
/// unknown token are each refused; only the credentialed, in-scope operator reads.
/// </summary>
public sealed class RedesignGm06OperatorHostTests
{
    [Fact]
    public async Task TheOperatorEntryAuthenticatesAndAuthorizesBeforeItReads()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        // The operators: gm-alice is scoped to tenant-a/game-a, viewer-carl ranks too low,
        // gm-eve is scoped elsewhere, gm-disabled is switched off.
        var operators = new RedesignOperatorStore(environment.DatabaseConnectionString);
        await operators.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "alice-token");
        await operators.RegisterOperatorAsync("viewer-carl", "viewer", ["tenant-a"], ["game-a"], "carl-token");
        await operators.RegisterOperatorAsync("gm-eve", "gm", ["tenant-b"], ["game-b"], "eve-token");
        await operators.RegisterOperatorAsync("gm-disabled", "gm", ["tenant-a"], ["game-a"], "disabled-token");
        await operators.SetOperatorEnabledAsync("gm-disabled", false);

        // One claimed room at epoch 1, owned by gs-gm06.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.ownership(kind,entity_id,owner_process_instance_id,epoch,
                    lease_until,status,version,tenant_id,game_id)
                VALUES('room','room-gm06','gs-gm06',1,now()+interval '60 second','Active',1,
                    'tenant-a','game-a')
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }

        using var certificate = environment.Certificate("tenant-a", "game-server", "gm06-game");
        using var channel = environment.Channel(environment.ControlPort, certificate, "control-server");
        var client = new Admin.AdminClient(channel);

        // (1) The credentialed, in-scope operator reads the authority.
        var gm = new GmOperatorClient(client, "gm-alice", "alice-token");
        var read = await gm.GetRoomEpochAsync("tenant-a", "game-a", "room-gm06");
        Assert.True(read.Found);
        Assert.Equal(1, read.Epoch);
        Assert.Equal("gs-gm06", read.OwnerInstanceId);
        Assert.Equal("Active", read.Status);

        // (2) An anchor identity — no operator credential — is refused.
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server", "gm06-streamer");
        using var streamerChannel = environment.Channel(environment.ControlPort, streamerCertificate, "control-server");
        var anchor = await Assert.ThrowsAsync<RpcException>(() => new Admin.AdminClient(streamerChannel)
            .GetRoomEpochAsync(new AdminGetRoomEpochRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-gm06", Operator = "gm-alice"
            }).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, anchor.StatusCode);

        // (3) A forged request operator — alice's credential, a different body identity — is refused.
        var forged = await Assert.ThrowsAsync<RpcException>(() => client
            .GetRoomEpochAsync(new AdminGetRoomEpochRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-gm06", Operator = "bob"
            }, new Metadata { { "authorization", "Bearer alice-token" } }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, forged.StatusCode);

        // (4) A scope that does not cover the target tenant is refused.
        var overScope = await Assert.ThrowsAsync<RpcException>(() => client
            .GetRoomEpochAsync(new AdminGetRoomEpochRequest
            {
                TenantId = "tenant-b", GameId = "game-b", RoomId = "room-x", Operator = "gm-alice"
            }, new Metadata { { "authorization", "Bearer alice-token" } }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, overScope.StatusCode);

        // (5) A process certificate is not an operator: no credential, refused.
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "gm06-control");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var processOnly = await Assert.ThrowsAsync<RpcException>(() => new Admin.AdminClient(controlChannel)
            .GetRoomEpochAsync(new AdminGetRoomEpochRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-gm06", Operator = "gm-alice"
            }).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, processOnly.StatusCode);

        // (6) A role below the command's minimum is refused.
        var lowRole = await Assert.ThrowsAsync<RpcException>(() => client
            .GetRoomEpochAsync(new AdminGetRoomEpochRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-gm06", Operator = "viewer-carl"
            }, new Metadata { { "authorization", "Bearer carl-token" } }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, lowRole.StatusCode);

        // (7) A disabled operator's credential no longer authenticates.
        var disabled = await Assert.ThrowsAsync<RpcException>(() => client
            .GetRoomEpochAsync(new AdminGetRoomEpochRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-gm06", Operator = "gm-disabled"
            }, new Metadata { { "authorization", "Bearer disabled-token" } }).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, disabled.StatusCode);

        // (8) An unknown token is refused.
        var invalid = await Assert.ThrowsAsync<RpcException>(() => client
            .GetRoomEpochAsync(new AdminGetRoomEpochRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-gm06", Operator = "gm-alice"
            }, new Metadata { { "authorization", "Bearer garbage" } }).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, invalid.StatusCode);
    }
}

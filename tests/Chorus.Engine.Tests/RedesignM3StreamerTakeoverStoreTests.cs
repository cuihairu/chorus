using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Streamer-entity ownership handover (M3-7b, architecture 10.3): the transfer transaction moves
/// the holder and bumps the epoch under the named-from guard, parks the row Recovering so the
/// renewal registrar leaves it alone, and the new holder's own claim confirms it — same holder
/// with a live lease keeps the epoch and returns the row Active. Claims are versioned
/// (FIX-AUTH-02): a streamer claim names the acting session and is admitted only while that
/// session is the streamer's current one, so a displaced holder's late claim cannot flip the
/// routing record back once a rotation moved the session on.
/// </summary>
public sealed class RedesignM3StreamerTakeoverStoreTests
{
    [Fact]
    public async Task TransferBumpsEpochUnderTheNamedHolderAndTheNewClaimConfirms()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await SeedSessionAsync(database.ConnectionString, "streamer-open-1", "sess-a1", 1);

        var claimed = await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-1", "streamer-a", 15, sessionId: "sess-a1", sessionVersion: 1);
        Assert.Equal(1, claimed.Epoch);
        Assert.Equal("Active", claimed.Status);

        // A stale command names the wrong holder; the entity that changed hands stays put.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.TransferOwnershipAsync(
            "tenant-a", "game-a", "streamer", "streamer-open-1", "streamer-b", "streamer-b", 15));
        // A scope that does not match the row is a visible refusal, not a silent second entity.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.TransferOwnershipAsync(
            "tenant-b", "game-a", "streamer", "streamer-open-1", "streamer-a", "streamer-b", 15));

        var moved = await control.TransferOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-1", "streamer-a", "streamer-b", 15);
        Assert.Equal("streamer-b", moved.OwnerInstanceId);
        Assert.Equal(2, moved.Epoch);
        Assert.Equal("Recovering", moved.Status);

        // A second transfer before the new holder confirms refuses: the row is not Active.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.TransferOwnershipAsync(
            "tenant-a", "game-a", "streamer", "streamer-open-1", "streamer-b", "streamer-c", 15));

        // The streamer resumed through B: the rotation made B's grant current (session v2), and
        // B's own claim — same holder, live lease — keeps the epoch and confirms Active.
        await SeedSessionAsync(database.ConnectionString, "streamer-open-1", "sess-b1", 2);
        var confirmed = await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-1", "streamer-b", 15, sessionId: "sess-b1", sessionVersion: 2);
        Assert.Equal("streamer-b", confirmed.OwnerInstanceId);
        Assert.Equal(2, confirmed.Epoch);
        Assert.Equal("Active", confirmed.Status);

        // A late claim from the displaced holder names the session the rotation retired: a
        // visible refusal, and the record stays with the current holder at the same epoch.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ClaimOwnershipAsync(
            "tenant-a", "game-a", "streamer", "streamer-open-1", "streamer-a", 15,
            sessionId: "sess-a1", sessionVersion: 1));
        var afterLate = await control.GetOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-1");
        Assert.Equal("streamer-b", afterLate!.OwnerInstanceId);
        Assert.Equal(2, afterLate.Epoch);
        Assert.Equal("Active", afterLate.Status);

        // A legitimate new login on A rotates the session to v3; its claim names the current
        // session and takes the record back — the different-holder rule still bumps the epoch.
        await SeedSessionAsync(database.ConnectionString, "streamer-open-1", "sess-a2", 3);
        var retaken = await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-1", "streamer-a", 15, sessionId: "sess-a2", sessionVersion: 3);
        Assert.Equal("streamer-a", retaken.OwnerInstanceId);
        Assert.Equal(3, retaken.Epoch);
        Assert.Equal("Active", retaken.Status);
    }

    [Fact]
    public async Task AStreamerClaimWithoutTheCurrentSessionIsRefusedVisibly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await SeedSessionAsync(database.ConnectionString, "streamer-open-3", "sess-c1", 4);

        // A streamer with no session state has no current session a claim could name.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ClaimOwnershipAsync(
            "tenant-a", "game-a", "streamer", "streamer-unknown", "streamer-a", 15,
            sessionId: "sess-x", sessionVersion: 1));
        // A claim with no session, or a non-positive version, never reroutes.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ClaimOwnershipAsync(
            "tenant-a", "game-a", "streamer", "streamer-open-3", "streamer-a", 15));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ClaimOwnershipAsync(
            "tenant-a", "game-a", "streamer", "streamer-open-3", "streamer-a", 15,
            sessionId: "sess-c1", sessionVersion: 0));
        // The right version under the wrong session id is still not the current session.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ClaimOwnershipAsync(
            "tenant-a", "game-a", "streamer", "streamer-open-3", "streamer-a", 15,
            sessionId: "sess-other", sessionVersion: 4));
        // The streamer belongs to another tenant: the claim is out of scope.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ClaimOwnershipAsync(
            "tenant-b", "game-a", "streamer", "streamer-open-3", "streamer-a", 15,
            sessionId: "sess-c1", sessionVersion: 4));
        // None of the refusals above created or moved a record.
        Assert.Null(await control.GetOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-3"));
        // The current session claims as before.
        var claimed = await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-3", "streamer-a", 15, sessionId: "sess-c1", sessionVersion: 4);
        Assert.Equal("streamer-a", claimed.OwnerInstanceId);
        Assert.Equal("Active", claimed.Status);
    }

    [Fact]
    public async Task RecoveringOwnershipIsNotRenewedEvenWhileTheHolderReportsLive()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await SeedSessionAsync(database.ConnectionString, "streamer-open-1", "sess-1", 1);
        await SeedSessionAsync(database.ConnectionString, "streamer-open-2", "sess-2", 1);

        await control.RegisterNodeAsync("node-a", "agent-1", "{}");
        await control.ReportProcessAsync("node-a", "agent-1", "node-a/streamer-server", "streamer-server",
            "streamer-a", 1, "Running", "Ready", "Healthy", "Accepting", "Steady", DateTime.UtcNow,
            "https://localhost:7502");
        var active = await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-1",
            "streamer-a", 6, sessionId: "sess-1", sessionVersion: 1);
        await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-2",
            "streamer-a", 6, sessionId: "sess-2", sessionVersion: 1);
        var moved = await control.TransferOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-2", "streamer-a", "streamer-b", 6);

        // The registrar renews only Active ownerships: the Recovering row keeps the lease the
        // handover wrote, so an uncompleted takeover expires on its own and becomes takeable.
        var renewed = await control.RenewLiveOwnershipsAsync(6, 15);
        var open1 = await control.GetOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-1");
        var open2 = await control.GetOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-2");
        Assert.True(renewed >= 1);
        Assert.True(open1!.LeaseUntilUtc > active.LeaseUntilUtc);
        Assert.Equal(moved.LeaseUntilUtc, open2!.LeaseUntilUtc);
        Assert.Equal("Recovering", open2.Status);
    }

    /// <summary>Seeds (or rotates) the streamer's session state: the current session a claim
    /// must name, at the version rotation left it.</summary>
    private static async Task SeedSessionAsync(
        string connectionString, string streamerId, string sessionId, long version)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO auth.streamer_state(streamer_id,tenant_id,current_session_version,current_session_id)
            VALUES (@streamer,'tenant-a',@version,@session)
            ON CONFLICT(streamer_id) DO UPDATE
            SET current_session_version=excluded.current_session_version,
                current_session_id=excluded.current_session_id
            """, connection);
        command.Parameters.AddWithValue("streamer", streamerId);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("session", sessionId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m3_streamer_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

        private async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = target.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

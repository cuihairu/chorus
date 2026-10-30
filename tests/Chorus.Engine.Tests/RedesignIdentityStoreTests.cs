using System.Security.Cryptography;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignIdentityStoreTests
{
    [Fact]
    public async Task PlatformIdentity_LoginTakeoverAndResumeRotationAreTenantScopedAndIdempotent()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignIdentityStore(database.ConnectionString);
        var identity = await store.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "opaque-open-id");
        var repeated = await store.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "opaque-open-id");
        Assert.Equal(identity.StreamerId, repeated.StreamerId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResolveIdentityAsync(
            "tenant-b", "simulator", "app-1", "opaque-open-id"));

        var firstSecret = RandomNumberGenerator.GetBytes(32);
        var login = await store.LoginAsync(identity.StreamerId, "gateway-1", "connection-1", firstSecret);
        Assert.Equal(1, login.SessionVersion);
        var nextSecret = RandomNumberGenerator.GetBytes(32);
        var rotated = await store.ResumeAsync(login.SessionId, "gateway-2", "connection-2", "resume-op-1",
            firstSecret, nextSecret);
        Assert.Equal(2, rotated.SessionVersion);
        Assert.False(rotated.Duplicate);
        var retried = await store.ResumeAsync(login.SessionId, "gateway-3", "connection-3", "resume-op-1",
            firstSecret, nextSecret);
        Assert.True(retried.Duplicate);
        Assert.Equal(rotated.SessionId, retried.SessionId);
        Assert.Equal(rotated.SessionVersion, retried.SessionVersion);
        await Assert.ThrowsAsync<SessionRevokedException>(() => store.ResumeAsync(login.SessionId,
            "gateway-2", "connection-2", "resume-op-different", firstSecret, RandomNumberGenerator.GetBytes(32)));

        var takeover = await store.LoginAsync(identity.StreamerId, "gateway-4", "connection-4",
            RandomNumberGenerator.GetBytes(32));
        Assert.Equal(3, takeover.SessionVersion);
        await Assert.ThrowsAsync<SessionReplacedException>(() => store.ResumeAsync(login.SessionId,
            "gateway-5", "connection-5", "resume-op-1", firstSecret, nextSecret));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM auth.session WHERE streamer_id=@streamer AND revoked_at IS NULL", connection);
        count.Parameters.AddWithValue("streamer", identity.StreamerId);
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ConnectionsAttachGenerationsStaleCloseDoesNotDetachAndStateReflectsFacts()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignIdentityStore(database.ConnectionString);
        var identity = await store.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "attach-open-id");
        var firstSecret = RandomNumberGenerator.GetBytes(32);
        var login = await store.LoginAsync(identity.StreamerId, "gateway-1", "connection-1", firstSecret);

        var snapshot = await store.GetSessionStateAsync(login.SessionId);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.Current);
        Assert.False(snapshot.Closed);
        Assert.Equal("tenant-a", snapshot.TenantId);
        Assert.Equal(1, snapshot.ConnectionGeneration);

        // A gateway switch attaches a new connection and increments the generation without rotation.
        var attach = await store.AttachConnectionAsync(login.SessionId, "gateway-2", "connection-2",
            "attach-op-1", firstSecret);
        Assert.Equal(2, attach.ConnectionGeneration);
        Assert.False(attach.Duplicate);
        var attachRetry = await store.AttachConnectionAsync(login.SessionId, "gateway-2", "connection-2",
            "attach-op-1", firstSecret);
        Assert.True(attachRetry.Duplicate);
        Assert.Equal(2, attachRetry.ConnectionGeneration);
        // Same operation with a different connection, or the wrong secret, is rejected.
        await Assert.ThrowsAsync<SessionRevokedException>(() => store.AttachConnectionAsync(login.SessionId,
            "gateway-3", "connection-3", "attach-op-1", firstSecret));
        await Assert.ThrowsAsync<SessionRevokedException>(() => store.AttachConnectionAsync(login.SessionId,
            "gateway-3", "connection-3", "attach-op-2", RandomNumberGenerator.GetBytes(32)));

        // Stale closes — old generation or wrong generation for the current pair — change nothing.
        Assert.False(await store.CloseConnectionAsync(login.SessionId, "gateway-1", "connection-1", 1));
        Assert.False(await store.CloseConnectionAsync(login.SessionId, "gateway-2", "connection-2", 99));
        var stillOnline = await store.GetSessionStateAsync(login.SessionId);
        Assert.False(stillOnline!.Closed);

        // The current generation closes the session.
        Assert.True(await store.CloseConnectionAsync(login.SessionId, "gateway-2", "connection-2", 2));
        Assert.True((await store.GetSessionStateAsync(login.SessionId))!.Closed);

        // A later attach re-binds the connection and clears the closed marker.
        var reopen = await store.AttachConnectionAsync(login.SessionId, "gateway-3", "connection-3",
            "attach-op-3", firstSecret);
        Assert.Equal(3, reopen.ConnectionGeneration);
        Assert.False((await store.GetSessionStateAsync(login.SessionId))!.Closed);

        // A new login revokes the old session; snapshots expose revoked and non-current.
        var takeover = await store.LoginAsync(identity.StreamerId, "gateway-4", "connection-4",
            RandomNumberGenerator.GetBytes(32));
        var revoked = await store.GetSessionStateAsync(login.SessionId);
        Assert.True(revoked!.Revoked);
        Assert.False(revoked.Current);
        var current = await store.GetSessionStateAsync(takeover.SessionId);
        Assert.True(current!.Current);
        Assert.False(current.Revoked);
    }

    [Fact]
    public async Task LoginAndResumeReportTheDisplacedConnectionForTheReplacementNotice()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignIdentityStore(database.ConnectionString);
        var identity = await store.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "open-m4");
        var firstSecret = RandomNumberGenerator.GetBytes(32);
        var login = await store.LoginAsync(identity.StreamerId, "gateway-1", "connection-1", firstSecret);
        Assert.Null(login.PreviousSessionId); // nothing was displaced by the very first grant.

        var secondSecret = RandomNumberGenerator.GetBytes(32);
        var resume = await store.ResumeAsync(login.SessionId, "gateway-2", "connection-2", "resume-op-1",
            firstSecret, secondSecret);
        Assert.Equal(login.SessionId, resume.PreviousSessionId);
        Assert.Equal("gateway-1", resume.PreviousGatewayInstanceId);
        Assert.Equal("connection-1", resume.PreviousConnectionId);
        Assert.Equal(1, resume.PreviousConnectionGeneration);
        // The exact retry replays the same grant and displaces nothing new.
        var retry = await store.ResumeAsync(login.SessionId, "gateway-3", "connection-3", "resume-op-1",
            firstSecret, secondSecret);
        Assert.True(retry.Duplicate);
        Assert.Null(retry.PreviousSessionId);
        Assert.Null(retry.PreviousConnectionId);

        var thirdSecret = RandomNumberGenerator.GetBytes(32);
        var takeover = await store.LoginAsync(identity.StreamerId, "gateway-4", "connection-4", thirdSecret);
        Assert.Equal(resume.SessionId, takeover.PreviousSessionId);
        Assert.Equal("gateway-2", takeover.PreviousGatewayInstanceId);
        Assert.Equal("connection-2", takeover.PreviousConnectionId);
    }

    [Fact]
    public async Task RevokeSessionIsIdempotentAndNamesTheDisplacedConnection()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignIdentityStore(database.ConnectionString);
        var identity = await store.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "open-m4-logout");
        var secret = RandomNumberGenerator.GetBytes(32);
        var login = await store.LoginAsync(identity.StreamerId, "gateway-1", "connection-1", secret);

        var revocation = await store.RevokeSessionAsync(login.SessionId);
        Assert.True(revocation.Applied);
        Assert.Equal(identity.StreamerId, revocation.StreamerId);
        Assert.Equal("gateway-1", revocation.DisplacedGatewayInstanceId);
        Assert.Equal("connection-1", revocation.DisplacedConnectionId);
        Assert.Equal(1, revocation.DisplacedConnectionGeneration);

        var duplicate = await store.RevokeSessionAsync(login.SessionId);
        Assert.False(duplicate.Applied);
        Assert.Null(duplicate.DisplacedConnectionId); // the first call already displaced it.

        // The revoked session can neither rotate nor be taken over by resume.
        await Assert.ThrowsAsync<SessionRevokedException>(() => store.ResumeAsync(login.SessionId,
            "gateway-2", "connection-2", "resume-after-logout", secret,
            RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public async Task GetSessionStateSeesTheRevocationImmediately()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignIdentityStore(database.ConnectionString);
        var identity = await store.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "open-m4-state");
        var login = await store.LoginAsync(identity.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        Assert.False((await store.GetSessionStateAsync(login.SessionId))!.Revoked);
        await store.RevokeSessionAsync(login.SessionId);
        var state = await store.GetSessionStateAsync(login.SessionId);
        Assert.True(state!.Revoked);
        Assert.False(state.Current);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m1_identity_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
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

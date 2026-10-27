using System.Security.Cryptography;
using Chorus.Data.Redesign;
using Chorus.DataServer;
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

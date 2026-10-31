using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Route-source registration (FIX-END-02): the StreamerServer's report names the committed
/// binding and its generation, and the control plane derives the platform identity from the
/// mount's live session — the join a platform-verified login created, so nothing a caller
/// claims can establish a route. A duplicate report replays the one row, an older generation
/// never overwrites the recorded newer registration, and the row is one per streamer and game
/// no matter how many mounts the live slot enters.
/// </summary>
public sealed class RedesignRouteSourceStoreTests
{
    [Fact]
    public async Task ADuplicateReportReplaysTheOneRow()
    {
        await using var arrange = await Arrange.MountedAsync();
        var first = await arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-a",
            arrange.StreamerId, arrange.First.BindingId, arrange.First.Generation);
        Assert.False(first.Duplicate);
        Assert.Equal(arrange.First.BindingId, first.Record.BindingId);
        Assert.Equal(1, first.Record.BindingGeneration);
        Assert.Equal("simulator", first.Record.Platform);
        Assert.Equal("live-key-route-source", first.Record.CanonicalLiveSessionKey);

        // The reply was lost, not the registration: the same report replays the one row.
        var replay = await arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-a",
            arrange.StreamerId, arrange.First.BindingId, arrange.First.Generation);
        Assert.True(replay.Duplicate);
        Assert.Equal(first.Record.BindingId, replay.Record.BindingId);
        Assert.Equal(first.Record.BindingGeneration, replay.Record.BindingGeneration);
        Assert.Single(await arrange.RouteSourceRowsAsync());

        // The next mount on the same live slot enters at a new generation: one row per
        // streamer and game, advanced in place.
        var second = await arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-a",
            arrange.StreamerId, arrange.Second.BindingId, arrange.Second.Generation);
        Assert.False(second.Duplicate);
        Assert.Equal(arrange.Second.BindingId, second.Record.BindingId);
        Assert.Equal(2, second.Record.BindingGeneration);
        Assert.Single(await arrange.RouteSourceRowsAsync());
    }

    [Fact]
    public async Task ALateGenerationNeverOverwritesTheNewerRegistration()
    {
        await using var arrange = await Arrange.MountedAsync();
        var current = await arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-a",
            arrange.StreamerId, arrange.Second.BindingId, arrange.Second.Generation);
        Assert.Equal(2, current.Record.BindingGeneration);

        // A late report of the first mount arrives after the second registered: refused,
        // recorded row untouched.
        await Assert.ThrowsAsync<StaleBindingGenerationException>(() =>
            arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-a", arrange.StreamerId,
                arrange.First.BindingId, arrange.First.Generation));
        // A report naming a generation the mount never held refuses the same way.
        await Assert.ThrowsAsync<StaleBindingGenerationException>(() =>
            arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-a", arrange.StreamerId,
                arrange.Second.BindingId, 99));
        var row = Assert.Single(await arrange.RouteSourceRowsAsync());
        Assert.Equal((arrange.Second.BindingId, 2L), (row.binding, row.generation));
    }

    [Fact]
    public async Task AnUnverifiedIdentityEstablishesNoRoute()
    {
        await using var arrange = await Arrange.MountedAsync();

        // A binding that does not exist registers nothing.
        await Assert.ThrowsAsync<RouteSourceUnverifiedException>(() =>
            arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-a", arrange.StreamerId,
                "binding-never-mounted", 1));
        // The real binding named under a foreign streamer, tenant or game is not that
        // caller's mount: unverified, refused, nothing written.
        await Assert.ThrowsAsync<RouteSourceUnverifiedException>(() =>
            arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-a", "streamer-someone-else",
                arrange.First.BindingId, arrange.First.Generation));
        await Assert.ThrowsAsync<RouteSourceUnverifiedException>(() =>
            arrange.Control.RegisterRouteSourceAsync("tenant-b", "game-a", arrange.StreamerId,
                arrange.First.BindingId, arrange.First.Generation));
        await Assert.ThrowsAsync<RouteSourceUnverifiedException>(() =>
            arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-b", arrange.StreamerId,
                arrange.First.BindingId, arrange.First.Generation));
        Assert.Empty(await arrange.RouteSourceRowsAsync());

        // The streamer's own report of its own mount still registers.
        var registered = await arrange.Control.RegisterRouteSourceAsync("tenant-a", "game-a",
            arrange.StreamerId, arrange.First.BindingId, arrange.First.Generation);
        Assert.False(registered.Duplicate);
        Assert.Equal("simulator", registered.Record.Platform);
    }

    /// <summary>One live session and two mounts on it — generations 1 and 2 of the same
    /// streamer's route source.</summary>
    private sealed class Arrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public string StreamerId { get; init; } = "";
        public BindingGrant First { get; init; } = null!;
        public BindingGrant Second { get; init; } = null!;

        public async Task<List<(string binding, long generation)>> RouteSourceRowsAsync()
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            var rows = new List<(string, long)>();
            await using var command = new NpgsqlCommand(
                "SELECT binding_id,binding_generation FROM control.route_source", connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetInt64(1)));
            return rows;
        }

        public static async Task<Arrange> MountedAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                var identityStore = new RedesignIdentityStore(database.ConnectionString);
                var bindingStore = new RedesignBindingStore(database.ConnectionString);
                var control = new RedesignControlStore(database.ConnectionString);
                var resolved = await identityStore.ResolveIdentityAsync("tenant-a", "simulator",
                    "app-1", "route-source-open-1");
                var streamerId = resolved.StreamerId;
                var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamerId,
                    "simulator", "app-1", "platform-room", "live-key-route-source");
                var session = await identityStore.LoginAsync(streamerId, "gateway-1",
                    "connection-1", RandomNumberGenerator.GetBytes(32));
                var first = await bindingStore.StartGameplayAsync("tenant-a", live,
                    session.SessionId, "game-a", "mount-route-source-1");
                var second = await bindingStore.StartGameplayAsync("tenant-a", live,
                    session.SessionId, "game-a", "mount-route-source-2");
                Assert.Equal(1, first.Generation);
                Assert.Equal(2, second.Generation);
                return new Arrange
                {
                    Database = database, IdentityStore = identityStore, BindingStore = bindingStore,
                    Control = control, StreamerId = streamerId, First = first, Second = second
                };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_route_src_" + Guid.NewGuid().ToString("N");
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
            await using (var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin))
                await drop.ExecuteNonQueryAsync();
        }
    }
}

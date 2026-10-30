using System.Security.Cryptography;
using System.Text;
using Chorus.ControlServer;
using Chorus.DataServer;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Retention guards (architecture 9 data retention, AT21): a settled round only releases
/// the frame prefix below the older of its last two snapshots — the recovery fallback pair —
/// never inside the retention window without an explicit override, never with undisposed
/// inputs, and never anything the recovery chain or the settlement evidence still needs.
/// A recovery read that pinned its view before a prune keeps its whole chain (FIX-READ-06),
/// and a read after the prune answers the honest pruned reality. Capacity pressure refuses
/// new rounds visibly with a counted reason instead of deleting quietly.
/// </summary>
public sealed class RedesignRetentionStoreTests
{
    [Fact]
    public async Task PruneRefusesActiveRoomsRetentionWindowsAndUndisposedInputs()
    {
        await using var arrange = await Arrange.CreateAsync();
        var retention = new RedesignRetentionStore(arrange.Database.ConnectionString);
        var runningRoom = await arrange.AdmittedAsync("room-ret-active", "ret-open-1", "ret-live-1");

        var active = await Assert.ThrowsAsync<RedesignRetentionException>(() =>
            retention.PruneRoundAsync("tenant-a", "game-a", runningRoom.RoomId, "prune-active"));
        Assert.Contains("status Running", active.Message);

        var settled = await arrange.SettledRoomAsync("room-ret-window", "ret-open-2", "ret-live-2");
        var windowed = await Assert.ThrowsAsync<RedesignRetentionException>(() =>
            retention.PruneRoundAsync("tenant-a", "game-a", settled.RoomId, "prune-window"));
        Assert.Contains("retention window", windowed.Message);

        // A pending delivery is undisposed input: retention refuses to free space over it,
        // even with the window overridden — the row is injected because the public flow
        // cannot leave a settled round with pending inputs by construction.
        var guarded = await arrange.SettledRoomAsync("room-ret-pending", "ret-open-3", "ret-live-3");
        await arrange.InjectPendingDeliveryAsync(guarded.RoomId);
        var refused = await Assert.ThrowsAsync<RedesignRetentionException>(() =>
            retention.PruneRoundAsync("tenant-a", "game-a", guarded.RoomId, "prune-pending",
                overrideRetentionWindow: true));
        Assert.Contains("undisposed inputs", refused.Message);
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var frames = new NpgsqlCommand(
                "SELECT count(*) FROM game.frame WHERE room_id=@room", connection);
            frames.Parameters.AddWithValue("room", guarded.RoomId);
            Assert.Equal(20L, Convert.ToInt64(await frames.ExecuteScalarAsync()));
        }
        await arrange.RemoveInjectedDeliveryAsync(guarded.RoomId);
        var pruned = await retention.PruneRoundAsync("tenant-a", "game-a", guarded.RoomId,
            "prune-pending-2", overrideRetentionWindow: true);
        Assert.Equal(0L, pruned.KeepTick);
    }

    [Fact]
    public async Task PruneReleasesOnlyThePrefixBelowTheOlderVerifiedAnchor()
    {
        await using var arrange = await Arrange.CreateAsync();
        var retention = new RedesignRetentionStore(arrange.Database.ConnectionString);
        // The admission snapshot at 0, a cadence checkpoint at 20 and store-verified deposits
        // at 40 and 80: the release boundary is the older of the last two of any kind (40) —
        // the fallback pair survives — and both rows below it leave with the prefix.
        var room = await arrange.SettledRoomAsync("room-ret-anchor", "ret-open-4", "ret-live-4",
            durationTicks: 120, checkpointTick: 20, anchorTicks: [40, 80]);
        long eventsBefore;
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var events = new NpgsqlCommand("SELECT count(*) FROM input.event", connection);
            eventsBefore = Convert.ToInt64(await events.ExecuteScalarAsync());
        }
        var pruned = await retention.PruneRoundAsync("tenant-a", "game-a", room.RoomId,
            "prune-anchor", overrideRetentionWindow: true);
        Assert.Equal(40L, pruned.KeepTick);
        Assert.Equal(39L, pruned.FramesDeleted);
        Assert.Equal(2L, pruned.SnapshotsDeleted);

        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var frames = new NpgsqlCommand(
                "SELECT count(*),min(tick_id),max(tick_id) FROM game.frame WHERE room_id=@room", connection))
            {
                frames.Parameters.AddWithValue("room", room.RoomId);
                await using var reader = await frames.ExecuteReaderAsync();
                await reader.ReadAsync();
                Assert.Equal(81L, reader.GetInt64(0));
                Assert.Equal(40L, reader.GetInt64(1));
                Assert.Equal(120L, reader.GetInt64(2));
            }
            await using (var snapshots = new NpgsqlCommand(
                "SELECT tick_id FROM game.snapshot WHERE room_id=@room ORDER BY tick_id", connection))
            {
                snapshots.Parameters.AddWithValue("room", room.RoomId);
                await using var reader = await snapshots.ExecuteReaderAsync();
                var ticks = new List<long>();
                while (await reader.ReadAsync()) ticks.Add(reader.GetInt64(0));
                Assert.Equal([40L, 80L], ticks);
            }
            // Raw input events, results, grants and assets outlive the frames: dedup, audit
            // and settlement evidence are not storage to reclaim (AT21).
            await using (var untouched = new NpgsqlCommand("""
                SELECT
                    (SELECT count(*) FROM input.event),
                    (SELECT count(*) FROM game.result WHERE room_id=@room),
                    (SELECT count(*) FROM settlement.grant WHERE room_id=@room AND status='Granted'),
                    (SELECT pruned_before_tick FROM game.room WHERE room_id=@room)
                """, connection))
            {
                untouched.Parameters.AddWithValue("room", room.RoomId);
                await using var reader = await untouched.ExecuteReaderAsync();
                await reader.ReadAsync();
                Assert.Equal(eventsBefore, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(2L, reader.GetInt64(2));
                Assert.Equal(40L, reader.GetInt64(3));
            }
        }
        // The recovery chain above the anchor is whole: both verified snapshots remain and
        // the frames walk contiguously from just past the older one to the head.
        var plan = await arrange.Recovery.LoadRecoveryAsync("tenant-a", "game-a", room.RoomId);
        Assert.NotNull(plan);
        Assert.Equal("Settled", plan!.Status);
        Assert.Equal(120L, plan.LastTick);
        Assert.Equal([80L, 40L], plan.Snapshots.Select(snapshot => snapshot.Tick).ToArray());
        Assert.Equal(80, plan.Frames.Count);
        Assert.Equal(41L, plan.Frames[0].Tick);
        Assert.Equal(120L, plan.Frames[^1].Tick);
        for (var index = 1; index < plan.Frames.Count; index++)
            Assert.Equal(plan.Frames[index - 1].Tick + 1, plan.Frames[index].Tick);

        // The same operation replays its receipt; a later operation finds nothing left.
        var retry = await retention.PruneRoundAsync("tenant-a", "game-a", room.RoomId,
            "prune-anchor", overrideRetentionWindow: true);
        Assert.True(retry.Duplicate);
        Assert.Equal(39L, retry.FramesDeleted);
        var later = await retention.PruneRoundAsync("tenant-a", "game-a", room.RoomId,
            "prune-anchor-2", overrideRetentionWindow: true);
        Assert.Equal(0L, later.FramesDeleted);
    }

    [Fact]
    public async Task PruneWithoutASecondVerifiedAnchorReleasesNothing()
    {
        await using var arrange = await Arrange.CreateAsync();
        var retention = new RedesignRetentionStore(arrange.Database.ConnectionString);
        // Only the admission snapshot at tick 0 exists: the older-of-last-two anchor is 0,
        // so the honest answer frees nothing rather than thinning the room's only snapshot.
        var room = await arrange.SettledRoomAsync("room-ret-single", "ret-open-5", "ret-live-5");
        var pruned = await retention.PruneRoundAsync("tenant-a", "game-a", room.RoomId,
            "prune-single", overrideRetentionWindow: true);
        Assert.Equal(0L, pruned.KeepTick);
        Assert.Equal(0L, pruned.FramesDeleted);
        Assert.Equal(0L, pruned.SnapshotsDeleted);
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var frames = new NpgsqlCommand(
                "SELECT count(*) FROM game.frame WHERE room_id=@room", connection);
            frames.Parameters.AddWithValue("room", room.RoomId);
            Assert.Equal(20L, Convert.ToInt64(await frames.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task APinnedRecoveryReadSurvivesAConcurrentPrune()
    {
        await using var arrange = await Arrange.CreateAsync();
        var retention = new RedesignRetentionStore(arrange.Database.ConnectionString);
        // Four anchors — 0/20/40/80 — over 120 frames: a depth-4 read spans the whole
        // history, while the prune below releases everything under the fallback pair (40).
        var room = await arrange.SettledRoomAsync("room-ret-pin", "ret-open-6", "ret-live-6",
            durationTicks: 120, checkpointTick: 20, anchorTicks: [40, 80]);

        // Pin the read inside its transaction, past the head row, before any material
        // loads (FIX-READ-06): retention commits a release while the read holds its view,
        // and the chain it is reading is never cut mid-read.
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var pinned = new RedesignRecoveryStore(arrange.Database.ConnectionString,
            afterHeadRead: () => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); });
        var loading = pinned.LoadRecoveryAsync("tenant-a", "game-a", room.RoomId, candidateDepth: 4);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pruned = await retention.PruneRoundAsync("tenant-a", "game-a", room.RoomId,
            "prune-pin", overrideRetentionWindow: true);
        release.SetResult();
        var plan = await loading.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(40L, pruned.KeepTick);
        Assert.Equal(39L, pruned.FramesDeleted);
        Assert.NotNull(plan);
        // The pinned view still holds every anchor and the whole contiguous walk: the read
        // that fixed its view first is not truncated by what committed underneath it.
        Assert.Equal([80L, 40L, 20L, 0L], plan!.Snapshots.Select(s => s.Tick).ToArray());
        Assert.Equal(120, plan.Frames.Count);
        Assert.Equal(1L, plan.Frames[0].Tick);
        Assert.Equal(120L, plan.Frames[^1].Tick);
        for (var index = 1; index < plan.Frames.Count; index++)
            Assert.Equal(plan.Frames[index - 1].Tick + 1, plan.Frames[index].Tick);

        // The same depth-4 read afterwards answers the honest pruned reality: the fallback
        // pair and the frames above it, fewer candidates than asked for — a deepening
        // recoverer suspends visibly (READ-04) — never a silent gap.
        var fresh = await arrange.Recovery.LoadRecoveryAsync("tenant-a", "game-a", room.RoomId,
            candidateDepth: 4);
        Assert.NotNull(fresh);
        Assert.Equal([80L, 40L], fresh!.Snapshots.Select(s => s.Tick).ToArray());
        Assert.Equal(80, fresh.Frames.Count);
        Assert.Equal(41L, fresh.Frames[0].Tick);

        // The prune moved neither the epoch nor the head — exactly the shrink the paging
        // cursor's row count exists to catch: a continuation from the pinned view refuses
        // to stitch pages across the released material.
        Assert.Equal(plan.Epoch, fresh.Epoch);
        Assert.Equal(plan.LastTick, fresh.LastTick);
        var cursor = new RecoveryPageToken(4, plan.Epoch, plan.LastTick, 0, 0,
            RecoveryPager.TotalRows(plan));
        Assert.True(RecoveryPager.ViewMoved(cursor, fresh, 4));
        Assert.False(RecoveryPager.ViewMoved(cursor, plan, 4));
    }

    [Fact]
    public async Task AdmissionCapRejectsNewRoundsVisiblyAndCounts()
    {
        await using var arrange = await Arrange.CreateAsync();
        var capped = new RedesignRoomStore(arrange.Database.ConnectionString, maxActiveRooms: 1);
        var first = await arrange.AdmittedAsync("room-cap-1", "cap-open-1", "cap-live-1",
            roomStore: capped);

        var (second, secondParticipant) = await arrange.MountedAsync("room-cap-2", "cap-open-2",
            "cap-live-2");
        await Assert.ThrowsAsync<RedesignCapacityException>(() => capped.StartRoundAsync(
            "tenant-a", "game-a", second.RoomId, "gs-1", second.Epoch, [secondParticipant],
            "1.0.0", "cfg-1", "pkg-1", "start-room-cap-2"));
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var counter = new NpgsqlCommand(
                "SELECT count FROM ops.capacity_rejection WHERE kind='start-round:active-rooms'",
                connection);
            Assert.Equal(1L, Convert.ToInt64(await counter.ExecuteScalarAsync()));
        }

        // Releasing the terminal state opens capacity again, and the refused operation left
        // no receipt behind: the same operation id admits cleanly on retry.
        await capped.AbortRoundAsync("tenant-a", "game-a", first.RoomId, "gs-1", first.Epoch,
            "test capacity release", "ret-cap-abort");
        await capped.StartRoundAsync("tenant-a", "game-a", second.RoomId, "gs-1", second.Epoch,
            [secondParticipant], "1.0.0", "cfg-1", "pkg-1", "start-room-cap-2");
        Assert.Equal("Preparing", (await capped.GetRoundAsync("tenant-a", "game-a", second.RoomId))!.Status);
        Assert.Equal("Aborted", (await capped.GetRoundAsync("tenant-a", "game-a", first.RoomId))!.Status);
    }

    private sealed class Arrange
    {
        public TemporaryDatabase Database = null!;
        public RedesignRoomStore RoomStore = null!;
        public RedesignFrameStore FrameStore = null!;
        public RedesignRecoveryStore Recovery = null!;
        public RedesignSettlementStore Settlement = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;

        public static async Task<Arrange> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                return new Arrange
                {
                    Database = database,
                    RoomStore = new RedesignRoomStore(database.ConnectionString),
                    FrameStore = new RedesignFrameStore(database.ConnectionString),
                    Recovery = new RedesignRecoveryStore(database.ConnectionString),
                    Settlement = new RedesignSettlementStore(database.ConnectionString),
                    IdentityStore = new RedesignIdentityStore(database.ConnectionString),
                    BindingStore = new RedesignBindingStore(database.ConnectionString),
                    Control = new RedesignControlStore(database.ConnectionString)
                };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public sealed record Room(string RoomId, string BindingId, string StreamerId, long Epoch);

        /// <summary>Mounts a binding and claims the room ownership; admission is the caller's.</summary>
        public async Task<(Room Room, RoundParticipant Participant)> MountedAsync(
            string roomId, string openId, string liveKey)
        {
            var streamer = await IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                "app-1", openId + "-open");
            var live = await BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                "simulator", "app-1", "platform-room", liveKey);
            var session = await IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                "connection-" + openId, RandomNumberGenerator.GetBytes(32));
            var binding = await BindingStore.StartGameplayAsync("tenant-a", live, session.SessionId,
                "game-a", "mount-" + openId);
            var claim = await Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", roomId, "gs-1", 60);
            return (new Room(roomId, binding.BindingId, streamer.StreamerId, claim.Epoch),
                new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host"));
        }

        /// <summary>Mounts, claims and admits the round through the given (possibly capped) store.</summary>
        public async Task<Room> AdmittedAsync(string roomId, string openId, string liveKey,
            RedesignRoomStore? roomStore = null)
        {
            var (room, participant) = await MountedAsync(roomId, openId, liveKey);
            await (roomStore ?? RoomStore).StartRoundAsync("tenant-a", "game-a", roomId, "gs-1",
                room.Epoch, [participant], "1.0.0", "cfg-1", "pkg-1", "start-" + roomId);
            await RoomStore.PrepareRoundAsync("tenant-a", "game-a", roomId, "gs-1", room.Epoch,
                [1, 2, 3], [4, 5, 6], 1, "prepare-" + roomId);
            await RoomStore.ActivateRoundAsync("tenant-a", "game-a", roomId, "gs-1", room.Epoch,
                "activate-" + roomId);
            return room;
        }

        /// <summary>
        /// Runs a full round to a settled state: frames 1..durationTicks with a store-verified
        /// head snapshot after each anchor tick and an optional unverified cadence checkpoint,
        /// then the final frame, grant settlement and finalization.
        /// </summary>
        public async Task<Room> SettledRoomAsync(string roomId, string openId, string liveKey,
            int durationTicks = 20, int? checkpointTick = null, long[]? anchorTicks = null)
        {
            var room = await AdmittedAsync(roomId, openId, liveKey);
            for (var tick = 1; tick <= durationTicks; tick++)
            {
                var payload = Encoding.UTF8.GetBytes($"state-{roomId}-{tick}");
                FrameCommit commit = new($"commit-{roomId}-{tick}", "gs-1", room.Epoch,
                    tick - 1, tick, [], SHA256.HashData(payload), [], "1.0.0", "cfg-1")
                {
                    SnapshotPayload = checkpointTick == tick ? payload : null,
                    SnapshotHash = checkpointTick == tick ? SHA256.HashData(payload) : null,
                    SnapshotSchemaVersion = checkpointTick == tick ? 1 : 0
                };
                if (tick == durationTicks)
                {
                    commit = commit with
                    {
                        Final = true, ResultJson = $$"""{"ranking":[{"faction":"red","score":100},{"faction":"blue","score":30}]}""",
                        PlanHash = SHA256.HashData(Encoding.UTF8.GetBytes("plan-" + roomId)),
                        Grants =
                        [
                            new PlannedGrant(room.StreamerId, "round-score", roomId + "/grant/1", "points", 100),
                            new PlannedGrant("guest-" + openId, "round-score", roomId + "/grant/2", "points", 30)
                        ]
                    };
                }
                await FrameStore.CommitFrameAsync("tenant-a", "game-a", roomId, commit);
                if (anchorTicks?.Contains(tick) == true)
                    await Recovery.PutSnapshotAsync("tenant-a", "game-a", roomId, "gs-1",
                        room.Epoch, tick, payload, SHA256.HashData(payload), 1,
                        $"snapshot-{roomId}-{tick}");
            }
            await Settlement.CommitResultAsync("tenant-a", "game-a", roomId, "settle-" + roomId);
            await Settlement.FinalizeRoundAsync("tenant-a", "game-a", roomId, "finalize-" + roomId);
            return room;
        }

        /// <summary>A pending delivery injected directly: the fence proof for undisposed input.</summary>
        public async Task InjectPendingDeliveryAsync(string roomId)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using (var insertEvent = new NpgsqlCommand("""
                INSERT INTO input.event(event_id,platform,app_id,source_dedup_key,payload_hash,
                    disposition,payload)
                VALUES (@event,'simulator','app-1',@dedup,@hash,'PendingAttribution','{}'::jsonb)
                """, connection))
            {
                insertEvent.Parameters.AddWithValue("event", "evt-injected-" + roomId);
                insertEvent.Parameters.AddWithValue("dedup", "dedup-injected-" + roomId);
                insertEvent.Parameters.AddWithValue("hash", SHA256.HashData([]));
                await insertEvent.ExecuteNonQueryAsync();
            }
            await using (var insertDelivery = new NpgsqlCommand("""
                INSERT INTO input.cursor(consumer_scope) VALUES (@scope)
                ON CONFLICT DO NOTHING
                """, connection))
            {
                insertDelivery.Parameters.AddWithValue("scope", "room:" + roomId);
                await insertDelivery.ExecuteNonQueryAsync();
            }
            await using (var insertDelivery = new NpgsqlCommand("""
                INSERT INTO input.delivery(event_id,consumer_scope,room_id,route_version,sequence,state)
                VALUES (@event,@scope,@room,1,999,'Pending')
                """, connection))
            {
                insertDelivery.Parameters.AddWithValue("event", "evt-injected-" + roomId);
                insertDelivery.Parameters.AddWithValue("scope", "room:" + roomId);
                insertDelivery.Parameters.AddWithValue("room", roomId);
                await insertDelivery.ExecuteNonQueryAsync();
            }
        }

        public async Task RemoveInjectedDeliveryAsync(string roomId)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM input.delivery WHERE event_id=@event", connection);
            command.Parameters.AddWithValue("event", "evt-injected-" + roomId);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m2_retention_" + Guid.NewGuid().ToString("N");
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
            await Chorus.Data.RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

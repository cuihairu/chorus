using System.Security.Cryptography;
using System.Text;
using Chorus.ControlServer;
using Chorus.Data.Redesign;
using Chorus.DataServer;
using Chorus.Redesign.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignRoundInputStoreTests
{
    [Fact]
    public async Task RoundAttributionFollowsHintAndFirstReceiveWindow()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        var (p1, b1, _) = await MountAsync(identityStore, bindingStore, "live-key-r1");
        var (p2, b2, _) = await MountAsync(identityStore, bindingStore, "live-key-r2");
        var claim1 = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim1.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-1");
        var claim9 = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-9", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-9", "gs-1", claim9.Epoch,
            [new RoundParticipant(b2.BindingId, p2.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-9");

        // Round 1 window: no hint follows the current room, a trusted hint lands in its room too.
        await eventStore.IngestAsync("simulator", "app-1", "batch-1",
        [
            new PlatformEventInput("gift-a", RedesignInputEventStore.Durable, "live-key-r1",
                """{"kind":"gift","amount":100}"""),
            new PlatformEventInput("gift-b", RedesignInputEventStore.Durable, "live-key-r1",
                """{"kind":"gift","amount":50}"""),
            new PlatformEventInput("gift-hint", RedesignInputEventStore.Durable, "live-key-r1",
                """{"kind":"gift","amount":10,"round_hint":"room-1"}""")
        ]);
        var first = await inputStore.RouteEventsAsync("simulator", "app-1", "route-1", 50);
        Assert.False(first.Duplicate);
        Assert.Equal(3, first.Events.Count);
        Assert.All(first.Events, item =>
        {
            Assert.Equal(RedesignRoundInputStore.Routed, item.Disposition);
            Assert.Equal("room-1", item.RoomId);
        });
        Assert.Equal([1L, 2L, 3L], first.Events.Select(item => item.Sequence!.Value).OrderBy(s => s).ToArray());

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand("""
                SELECT count(*),min(sequence),max(sequence) FROM input.delivery
                WHERE consumer_scope='room:room-1' AND state='Pending' AND route_version=1
                """, connection))
            await using (var reader = await deliveries.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(3L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(3L, reader.GetInt64(2));
            }
            // The consumption watermark belongs to the frame committer (M2-2), not to routing.
            await using (var cursor = new NpgsqlCommand("""
                SELECT next_delivery_sequence,consumed_contiguous_sequence FROM input.cursor
                WHERE consumer_scope='room:room-1'
                """, connection))
            await using (var reader = await cursor.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(4L, reader.GetInt64(0));
                Assert.Equal(0L, reader.GetInt64(1));
            }
            await using (var routed = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE disposition='Routed' AND attributed_room_id='room-1'",
                connection))
                Assert.Equal(3L, Convert.ToInt64(await routed.ExecuteScalarAsync()));
        }

        // Between rounds the pointer is empty: a no-hint event lands out of round with no room.
        await roomStore.AbortRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim1.Epoch,
            "host-left", "abort-1");
        await eventStore.IngestAsync("simulator", "app-1", "batch-2",
        [
            new PlatformEventInput("gift-orphan", RedesignInputEventStore.Durable, "live-key-r1",
                """{"kind":"gift","amount":5}""")
        ]);
        var orphan = await inputStore.RouteEventsAsync("simulator", "app-1", "route-2", 50);
        var orphanItem = Assert.Single(orphan.Events);
        Assert.Equal(RedesignRoundInputStore.OutOfRound, orphanItem.Disposition);
        Assert.Null(orphanItem.RoomId);

        // Generation 2 window: a no-hint event follows the new round; a trusted hint at the
        // aborted round records its terminal attribution; another binding's round is ambiguous.
        var claim2 = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-2", "gs-2", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-2", "gs-2", claim2.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-2");
        await eventStore.IngestAsync("simulator", "app-1", "batch-3",
        [
            new PlatformEventInput("gift-new", RedesignInputEventStore.Durable, "live-key-r1",
                """{"kind":"gift","amount":20}"""),
            new PlatformEventInput("gift-late", RedesignInputEventStore.Durable, "live-key-r1",
                """{"kind":"gift","amount":30,"round_hint":"room-1"}"""),
            new PlatformEventInput("gift-foreign", RedesignInputEventStore.Durable, "live-key-r1",
                """{"kind":"gift","amount":40,"round_hint":"room-9"}""")
        ]);
        var second = await inputStore.RouteEventsAsync("simulator", "app-1", "route-3", 50);
        Assert.Equal(3, second.Events.Count);
        // Order inside the reply follows receive order; dispositions distinguish the cases.
        var dispositions = second.Events.Select(item => item.Disposition).OrderBy(d => d).ToArray();
        Assert.Equal(
        [
            RedesignRoundInputStore.AmbiguousRound, RedesignRoundInputStore.OutOfRound,
            RedesignRoundInputStore.Routed
        ], dispositions);
        var routedNew = Assert.Single(second.Events, item => item.Disposition == RedesignRoundInputStore.Routed);
        Assert.Equal("room-2", routedNew.RoomId);
        Assert.Equal(1L, routedNew.Sequence);
        var late = Assert.Single(second.Events, item => item.Disposition == RedesignRoundInputStore.OutOfRound);
        Assert.Equal("room-1", late.RoomId);
        Assert.Null(late.Sequence);
        Assert.Null(Assert.Single(second.Events,
            item => item.Disposition == RedesignRoundInputStore.AmbiguousRound).RoomId);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var ambiguous = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE disposition='AmbiguousRound'", connection))
                Assert.Equal(1L, Convert.ToInt64(await ambiguous.ExecuteScalarAsync()));
            await using (var terminal = new NpgsqlCommand("""
                SELECT attributed_room_id FROM input.event WHERE source_dedup_key='gift-late'
                """, connection))
                Assert.Equal("room-1", await terminal.ExecuteScalarAsync());
            await using (var terminalOrphan = new NpgsqlCommand("""
                SELECT attributed_room_id IS NULL FROM input.event WHERE source_dedup_key='gift-orphan'
                """, connection))
                Assert.True(Convert.ToBoolean(await terminalOrphan.ExecuteScalarAsync()));
        }

        // A redelivered duplicate answers with its original committed room attribution.
        var redelivered = await eventStore.IngestAsync("simulator", "app-1", "batch-4",
        [
            new PlatformEventInput("gift-a", RedesignInputEventStore.Durable, "live-key-r1",
                """{"kind":"gift","amount":100}""")
        ]);
        Assert.True(redelivered.Receipts[0].Duplicate);
        Assert.Equal("room-1", redelivered.Receipts[0].RoomId);
    }

    [Fact]
    public async Task RoutingCrashInjectionRollsBackAndRetryConverges()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var armed = false;
        var inputStore = new RedesignRoundInputStore(database.ConnectionString,
            () =>
            {
                if (armed) throw new InvalidOperationException("simulated process death mid-routing");
            });

        var (p1, b1, _) = await MountAsync(identityStore, bindingStore, "live-key-c");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-1");
        await eventStore.IngestAsync("simulator", "app-1", "batch-1",
        [
            new PlatformEventInput("gift-1", RedesignInputEventStore.Durable, "live-key-c",
                """{"kind":"gift","amount":100}"""),
            new PlatformEventInput("gift-2", RedesignInputEventStore.Durable, "live-key-c",
                """{"kind":"gift","amount":50}""")
        ]);

        // The router dies after the cursor allocation, before the commit: everything rolls back.
        armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            inputStore.RouteEventsAsync("simulator", "app-1", "route-1", 50));
        armed = false;
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery", connection))
                Assert.Equal(0L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
            await using (var cursors = new NpgsqlCommand("SELECT count(*) FROM input.cursor", connection))
                Assert.Equal(0L, Convert.ToInt64(await cursors.ExecuteScalarAsync()));
            await using (var attributed = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE disposition='Attributed' AND attributed_room_id IS NULL",
                connection))
                Assert.Equal(2L, Convert.ToInt64(await attributed.ExecuteScalarAsync()));
        }

        // The same operation id retries cleanly and converges; a further retry is a duplicate.
        var recovered = await inputStore.RouteEventsAsync("simulator", "app-1", "route-1", 50);
        Assert.False(recovered.Duplicate);
        Assert.Equal(2, recovered.Events.Count);
        Assert.All(recovered.Events, item => Assert.Equal(RedesignRoundInputStore.Routed, item.Disposition));
        Assert.Equal([1L, 2L], recovered.Events.Select(item => item.Sequence!.Value).OrderBy(s => s).ToArray());
        var duplicate = await inputStore.RouteEventsAsync("simulator", "app-1", "route-1", 50);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(recovered.Events.Select(item => item.EventId),
            duplicate.Events.Select(item => item.EventId));

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery", connection))
                Assert.Equal(2L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
            await using (var cursor = new NpgsqlCommand("""
                SELECT next_delivery_sequence FROM input.cursor WHERE consumer_scope='room:room-1'
                """, connection))
                Assert.Equal(3L, Convert.ToInt64(await cursor.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ClientInputValidatesSessionAndParticipationAndIsIdempotent()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        var (p1, b1, session) = await MountAsync(identityStore, bindingStore, "live-key-cli");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-1");

        const string payload = """{"action":"boost","level":2}""";
        var accepted = await inputStore.AcceptClientInputAsync("tenant-a", "game-a", session.SessionId,
            "room-1", claim.Epoch, "cmd-1", payload);
        Assert.Equal("cmd-1", accepted.CommandId);
        Assert.Equal("room-1", accepted.RoomId);
        Assert.Equal(1L, accepted.Sequence);
        Assert.Equal(1L, accepted.RouteVersion);
        Assert.False(accepted.Duplicate);

        // The exact retry replays the receipt: one command, one delivery.
        var retry = await inputStore.AcceptClientInputAsync("tenant-a", "game-a", session.SessionId,
            "room-1", claim.Epoch, "cmd-1", payload);
        Assert.True(retry.Duplicate);
        Assert.Equal(1L, retry.Sequence);
        // The same command id with a different payload conflicts.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => inputStore.AcceptClientInputAsync(
            "tenant-a", "game-a", session.SessionId, "room-1", claim.Epoch, "cmd-1",
            """{"action":"boost","level":9}"""));

        // A takeover revokes the old session; the current session still commands.
        var takeover = await identityStore.LoginAsync(p1.StreamerId, "gateway-1", "connection-9",
            RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<SessionRevokedException>(() => inputStore.AcceptClientInputAsync(
            "tenant-a", "game-a", session.SessionId, "room-1", claim.Epoch, "cmd-2", payload));
        var current = await inputStore.AcceptClientInputAsync("tenant-a", "game-a", takeover.SessionId,
            "room-1", claim.Epoch, "cmd-2", payload);
        Assert.Equal(2L, current.Sequence);

        // A stale room epoch is fenced; a closed participation after abort is a visible rejection.
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => inputStore.AcceptClientInputAsync(
            "tenant-a", "game-a", takeover.SessionId, "room-1", claim.Epoch + 3, "cmd-3", payload));
        await roomStore.AbortRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            "host-left", "abort-1");
        await Assert.ThrowsAsync<RoundAdmissionException>(() => inputStore.AcceptClientInputAsync(
            "tenant-a", "game-a", takeover.SessionId, "room-1", claim.Epoch, "cmd-4", payload));

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var events = new NpgsqlCommand("""
                SELECT count(*),count(distinct source_dedup_key) FROM input.event WHERE platform='@chorus'
                """, connection))
            await using (var reader = await events.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(2L, reader.GetInt64(1));
            }
            await using (var deliveries = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE consumer_scope='room:room-1'", connection))
                Assert.Equal(2L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
            await using (var dispositions = new NpgsqlCommand("""
                SELECT count(*) FROM input.event
                WHERE platform='@chorus' AND disposition='Routed' AND attributed_room_id='room-1'
                """, connection))
                Assert.Equal(2L, Convert.ToInt64(await dispositions.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task WithdrawalIsIdempotentAndFreesTheSeatWhileTheRoomRunsOn()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        var (p1, b1, s1) = await MountAsync(identityStore, bindingStore, "live-key-w1");
        var (p2, b2, s2) = await MountAsync(identityStore, bindingStore, "live-key-w2");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-w1", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-w1", "gs-1", claim.Epoch,
        [
            new RoundParticipant(b1.BindingId, p1.StreamerId, "Host"),
            new RoundParticipant(b2.BindingId, p2.StreamerId, "Challenger")
        ], "1.0.0", "cfg-1", "pkg-1", "start-w1");

        var withdrawn = await inputStore.WithdrawParticipationAsync("tenant-a", "game-a", s1.SessionId,
            "room-w1", "leave-1");
        Assert.Equal("room-w1", withdrawn.RoomId);
        Assert.Equal("Left", withdrawn.State);
        Assert.False(withdrawn.Duplicate);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var rows = new NpgsqlCommand("""
                SELECT p.participant_id,p.state,(b.current_room_id IS NOT DISTINCT FROM 'room-w1')
                FROM game.participation p
                JOIN live.binding b ON b.binding_id=p.binding_id
                WHERE p.room_id='room-w1'
                """, connection))
            {
                var byParticipant = new Dictionary<string, (string State, bool InRoom)>();
                await using (var reader = await rows.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                        byParticipant[reader.GetString(0)] = (reader.GetString(1), reader.GetBoolean(2));
                }
                Assert.Equal(("Left", false), byParticipant[p1.StreamerId]);
                Assert.Equal(("Active", true), byParticipant[p2.StreamerId]);
            }
            // The room itself never moved: the leaver's exit is not the round's end.
            await using (var room = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id='room-w1'", connection))
                Assert.Equal("Preparing", await room.ExecuteScalarAsync());
        }

        var retry = await inputStore.WithdrawParticipationAsync("tenant-a", "game-a", s1.SessionId,
            "room-w1", "leave-1");
        Assert.True(retry.Duplicate);
        Assert.Equal("Left", retry.State);
        // A second operation on an already-Left participation withdraws quietly — the
        // leaver's retry after a lost reply is never punished.
        var again = await inputStore.WithdrawParticipationAsync("tenant-a", "game-a", s1.SessionId,
            "room-w1", "leave-1b");
        Assert.False(again.Duplicate);
        Assert.Equal("Left", again.State);
    }

    [Fact]
    public async Task WithdrawalFencesTheSessionTheRoomAndTheGame()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        var (p1, b1, s1) = await MountAsync(identityStore, bindingStore, "live-key-w3");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-w3", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-w3", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-w3");

        // A room this streamer never joined.
        await Assert.ThrowsAsync<RoundAdmissionException>(() => inputStore.WithdrawParticipationAsync(
            "tenant-a", "game-a", s1.SessionId, "room-none", "leave-x"));
        // The wrong game names a binding that does not belong to it.
        await Assert.ThrowsAsync<RoundAdmissionException>(() => inputStore.WithdrawParticipationAsync(
            "tenant-a", "game-b", s1.SessionId, "room-w3", "leave-x"));
        // A session another login replaced no longer speaks for the mount.
        var takeover = await identityStore.LoginAsync(p1.StreamerId, "gateway-1", "connection-w3",
            RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<SessionRevokedException>(() => inputStore.WithdrawParticipationAsync(
            "tenant-a", "game-a", s1.SessionId, "room-w3", "leave-x"));
        // The room ending the participation first makes withdrawal a visible rejection —
        // the abort closes the still-active seat, and a closed seat cannot leave.
        await roomStore.AbortRoundAsync("tenant-a", "game-a", "room-w3", "gs-1", claim.Epoch,
            "host-left", "abort-w3");
        await Assert.ThrowsAsync<RoundAdmissionException>(() => inputStore.WithdrawParticipationAsync(
            "tenant-a", "game-a", takeover.SessionId, "room-w3", "leave-late"));
    }

    [Fact]
    public async Task CommandsStopAtTheSeatThatLeftWhileTheRoomAcceptsTheRest()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        var (p1, b1, s1) = await MountAsync(identityStore, bindingStore, "live-key-w4");
        var (p2, b2, s2) = await MountAsync(identityStore, bindingStore, "live-key-w5");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-w4", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-w4", "gs-1", claim.Epoch,
        [
            new RoundParticipant(b1.BindingId, p1.StreamerId, "Host"),
            new RoundParticipant(b2.BindingId, p2.StreamerId, "Challenger")
        ], "1.0.0", "cfg-1", "pkg-1", "start-w4");

        await inputStore.WithdrawParticipationAsync("tenant-a", "game-a", s1.SessionId, "room-w4",
            "leave-1");
        const string payload = """{"action":"boost","level":2}""";
        await Assert.ThrowsAsync<RoundAdmissionException>(() => inputStore.AcceptClientInputAsync(
            "tenant-a", "game-a", s1.SessionId, "room-w4", claim.Epoch, "cmd-left", payload));
        var staying = await inputStore.AcceptClientInputAsync("tenant-a", "game-a", s2.SessionId,
            "room-w4", claim.Epoch, "cmd-stays", payload);
        Assert.Equal(1L, staying.Sequence);
    }

    [Fact]
    public async Task AFinalizedRoundAcceptsALeftParticipation()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);
        var frameStore = new RedesignFrameStore(database.ConnectionString);
        var settlementStore = new RedesignSettlementStore(database.ConnectionString);

        var (p1, b1, s1) = await MountAsync(identityStore, bindingStore, "live-key-w6");
        var (p2, b2, s2) = await MountAsync(identityStore, bindingStore, "live-key-w7");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-w6", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-w6", "gs-1", claim.Epoch,
        [
            new RoundParticipant(b1.BindingId, p1.StreamerId, "Host"),
            new RoundParticipant(b2.BindingId, p2.StreamerId, "Challenger")
        ], "1.0.0", "cfg-1", "pkg-1", "start-w6");
        await roomStore.PrepareRoundAsync("tenant-a", "game-a", "room-w6", "gs-1", claim.Epoch,
            [1, 2, 3], [4, 5, 6], 1, "prepare-w6");
        await roomStore.ActivateRoundAsync("tenant-a", "game-a", "room-w6", "gs-1", claim.Epoch,
            "activate-w6");
        await inputStore.WithdrawParticipationAsync("tenant-a", "game-a", s1.SessionId, "room-w6",
            "leave-1");
        const string resultJson = """{"winner":"red"}""";
        await frameStore.CommitFrameAsync("tenant-a", "game-a", "room-w6",
            new FrameCommit("final-w6", "gs-1", claim.Epoch, 0, 1, [], [9, 9, 9], [],
                "1.0.0", "cfg-1", Final: true, ResultJson: resultJson,
                PlanHash: SHA256.HashData(Encoding.UTF8.GetBytes(resultJson)),
                Grants: [new PlannedGrant("p1", "round-score", "room-w6/grant/p1", "points", 100)]));
        await settlementStore.CommitResultAsync("tenant-a", "game-a", "room-w6", "settle-w6");
        var finalized = await settlementStore.FinalizeRoundAsync("tenant-a", "game-a", "room-w6",
            "finalize-w6");
        Assert.Equal("Settled", finalized.Status);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var rows = new NpgsqlCommand("""
            SELECT p.participant_id,p.state,(b.current_room_id IS NULL)
            FROM game.participation p
            JOIN live.binding b ON b.binding_id=p.binding_id
            WHERE p.room_id='room-w6'
            """, connection))
        {
            var byParticipant = new Dictionary<string, (string State, bool Freed)>();
            await using (var reader = await rows.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    byParticipant[reader.GetString(0)] = (reader.GetString(1), reader.GetBoolean(2));
            }
            // The leaver stays Left — finalize's close only touches Active/Offline — and both
            // pointers end freed: the withdrawal cleared its own, the terminator cleared the rest.
            Assert.Equal(("Left", true), byParticipant[p1.StreamerId]);
            Assert.Equal(("Closed", true), byParticipant[p2.StreamerId]);
        }
    }

    private static async Task<(ResolvedStreamer Streamer, BindingGrant Binding, SessionGrant Session)> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string canonicalKey)
    {
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1",
            "route-open-" + canonicalKey);
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator", "app-1",
            "platform-room", canonicalKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var binding = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");
        return (streamer, binding, session);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m2_routing_" + Guid.NewGuid().ToString("N");
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

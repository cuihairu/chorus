using System.Security.Cryptography;
using System.Text;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
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
    public async Task ConcurrentRoutingPassesNeverDoubleDeliverOrRollBack()
    {
        // Audit P0-3's surface: two overlapping input.route passes over the same candidate
        // window. The binding locks of pass 2 serialize any two passes that share an event,
        // and pass 3's skip-locked claim keeps that safety local to the event row itself —
        // so the union of both results delivers every event exactly once, no pass rolls back
        // on input.delivery's (event_id, consumer_scope) primary key, and the window is
        // empty afterwards.
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        var (p1, b1, _) = await MountAsync(identityStore, bindingStore, "live-key-cc1");
        var (p2, b2, _) = await MountAsync(identityStore, bindingStore, "live-key-cc2");
        var claim1 = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-cc1", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-cc1", "gs-1", claim1.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-cc1");
        var claim2 = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-cc2", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-cc2", "gs-1", claim2.Epoch,
            [new RoundParticipant(b2.BindingId, p2.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-cc2");
        await eventStore.IngestAsync("simulator", "app-1", "batch-cc1",
            Enumerable.Range(1, 20).Select(i => new PlatformEventInput($"gift-c1-{i}",
                RedesignInputEventStore.Durable, "live-key-cc1",
                $$"""{"kind":"gift","amount":{{i}}}""")).ToArray());
        await eventStore.IngestAsync("simulator", "app-1", "batch-cc2",
            Enumerable.Range(1, 20).Select(i => new PlatformEventInput($"gift-c2-{i}",
                RedesignInputEventStore.Durable, "live-key-cc2",
                $$"""{"kind":"gift","amount":{{i}}}""")).ToArray());

        var passes = await Task.WhenAll(
            inputStore.RouteEventsAsync("simulator", "app-1", "route-cc-a", 500),
            inputStore.RouteEventsAsync("simulator", "app-1", "route-cc-b", 500));

        var routed = passes.SelectMany(pass => pass.Events)
            .Where(item => item.Disposition == RedesignRoundInputStore.Routed).ToArray();
        Assert.Equal(40, routed.Length);
        Assert.Equal(40, routed.Select(item => item.EventId).Distinct().Count());
        Assert.All(routed, item =>
        {
            Assert.True(item.Sequence is > 0);
            Assert.True(item.RoomId is "room-cc1" or "room-cc2");
        });
        // The event id in a routing reply is the internal id, not the source dedup key, so
        // each room's accepted set is pinned against the store's own attribution: room-cc1's
        // deliveries are exactly its binding's twenty events, room-cc2's likewise.
        foreach (var room in (string[])["room-cc1", "room-cc2"])
        {
            var roomItems = routed.Where(item => item.RoomId == room).ToArray();
            Assert.Equal(20, roomItems.Length);
            Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i).Order().ToArray(),
                roomItems.Select(item => item.Sequence!.Value).Order().ToArray());
        }

        // One delivery per event, the cursors stand past the last sequence, and a follow-up
        // pass finds nothing: the window drained exactly once.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand("SELECT count(*) FROM input.delivery", connection))
                Assert.Equal(40L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
            await using (var doubled = new NpgsqlCommand("""
                SELECT count(*) FROM (SELECT event_id FROM input.delivery GROUP BY event_id HAVING count(*) > 1) AS d
                """, connection))
                Assert.Equal(0L, Convert.ToInt64(await doubled.ExecuteScalarAsync()));
            foreach (var (room, prefix) in ((string, string)[]) [("room-cc1", "gift-c1-"), ("room-cc2", "gift-c2-")])
                await using (var attribution = new NpgsqlCommand("""
                    SELECT count(*) FROM input.delivery AS d
                    JOIN input.event AS e ON e.event_id = d.event_id
                    WHERE d.consumer_scope = @scope AND e.source_dedup_key LIKE @prefix
                    """, connection))
                {
                    attribution.Parameters.AddWithValue("scope", "room:" + room);
                    attribution.Parameters.AddWithValue("prefix", prefix + "%");
                    Assert.Equal(20L, Convert.ToInt64(await attribution.ExecuteScalarAsync()));
                }
            foreach (var room in (string[])["room-cc1", "room-cc2"])
                await using (var cursor = new NpgsqlCommand(
                    "SELECT next_delivery_sequence FROM input.cursor WHERE consumer_scope=@scope", connection))
                {
                    cursor.Parameters.AddWithValue("scope", "room:" + room);
                    Assert.Equal(21L, Convert.ToInt64(await cursor.ExecuteScalarAsync()));
                }
        }
        var followUp = await inputStore.RouteEventsAsync("simulator", "app-1", "route-cc-c", 500);
        Assert.Empty(followUp.Events);
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
    public async Task ClientInputRateLimitRefusesTheWindowAndSlidesIt()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        var (p1, b1, session) = await MountAsync(identityStore, bindingStore, "live-key-rate");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-rate", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-rate", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-1");

        const string payload = """{"action":"boost","level":2}""";
        // FIX-SYNC-09, load-independent: the budget is MaxClientCommandsPerWindow accepted
        // commands per rolling window. Seeding the window with exactly the cap's worth of this
        // binding's own @chorus rows is deterministic — a sequential loop of one-hundred real
        // RPCs would race the 10-second window on a slow shared machine, where the loop itself
        // takes longer than the window and the count never reaches the cap. The seed writes the
        // exact rows the store's count reads (platform, binding_id, received_at); the cap's own
        // edge is then proven by one real command at and one real command past it.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO input.event(event_id,platform,app_id,source_dedup_key,payload_hash,
                    disposition,binding_id,payload)
                SELECT 'seed-rate-' || g, '@chorus', 'game-a', 'seed-rate-' || g,
                    '\x0000'::bytea, 'Routed', @binding, @payload::jsonb
                FROM generate_series(0, @cap - 1) g
                """, connection))
            {
                seed.Parameters.AddWithValue("binding", b1.BindingId);
                seed.Parameters.AddWithValue("payload", payload);
                seed.Parameters.AddWithValue("cap", RedesignRoundInputStore.MaxClientCommandsPerWindow);
                await seed.ExecuteNonQueryAsync();
            }
        }
        // The (cap+1)th command is refused by the window before it is ever inserted — and the
        // refusal is a rejection, not a queue: nothing of the refused command is persisted.
        var refusedId = "cmd-" + RedesignRoundInputStore.MaxClientCommandsPerWindow;
        await Assert.ThrowsAsync<ClientCommandRateExceededException>(() => inputStore.AcceptClientInputAsync(
            "tenant-a", "game-a", session.SessionId, "room-rate", claim.Epoch, refusedId, payload));
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var persisted = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE event_id=@id", connection))
            {
                persisted.Parameters.AddWithValue("id", refusedId);
                Assert.Equal(0L, Convert.ToInt64(await persisted.ExecuteScalarAsync()));
            }
        }

        // The window slides: backdating every seeded command beyond the window empties the
        // count, so a command that was just refused is accepted now — the cap bounds a window,
        // not a participant's lifetime.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var backdate = new NpgsqlCommand("""
                UPDATE input.event SET received_at = received_at - interval '11 seconds'
                WHERE platform='@chorus' AND binding_id=@binding
                """, connection))
            {
                backdate.Parameters.AddWithValue("binding", b1.BindingId);
                await backdate.ExecuteNonQueryAsync();
            }
        }
        var afterSlide = await inputStore.AcceptClientInputAsync("tenant-a", "game-a", session.SessionId,
            "room-rate", claim.Epoch, "cmd-after-slide", payload);
        Assert.False(afterSlide.Duplicate);

        // The accepted command itself is a @chorus row in the window — so a real accepted
        // command accumulates toward the cap, not just the seeded rows.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var inWindow = new NpgsqlCommand("""
                SELECT count(*) FROM input.event
                WHERE platform='@chorus' AND binding_id=@binding
                  AND received_at >= clock_timestamp() - make_interval(secs => @window)
                """, connection))
            {
                inWindow.Parameters.AddWithValue("binding", b1.BindingId);
                inWindow.Parameters.AddWithValue("window", RedesignRoundInputStore.ClientCommandWindowSeconds);
                Assert.Equal(1L, Convert.ToInt64(await inWindow.ExecuteScalarAsync()));
            }
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
        await EndLadderEvidence.StandAsync(database.ConnectionString, "room-w6");
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

    // FIX-SYNC-07: the paused-input gate (authoritative-sync.md:52) — a room prepared to a
    // canonical offline declaration, activated, then parked at its committed head (the
    // prepared head is tick 0 / version 1). Every paused-room test below runs the real
    // store ladder, so the gate is exercised against real room facts, not a stub.
    private static async Task<(string BindingId, long Epoch)> PauseRoomAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore,
        RedesignControlStore control, RedesignRoomStore roomStore, string canonicalKey,
        string canonical, string roomId, string owner)
    {
        var (p1, b1, _) = await MountAsync(identityStore, bindingStore, canonicalKey);
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", roomId, owner, 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", roomId, owner, claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-" + roomId);
        await roomStore.PrepareRoundAsync("tenant-a", "game-a", roomId, owner, claim.Epoch,
            [1, 2, 3], [4, 5, 6], 1, "prepare-" + roomId, offlinePolicy: canonical);
        await roomStore.ActivateRoundAsync("tenant-a", "game-a", roomId, owner, claim.Epoch,
            "activate-" + roomId);
        await roomStore.PauseRoundAsync("tenant-a", "game-a", roomId, owner, claim.Epoch,
            "all control sessions offline", "pause-" + roomId, 0, 1);
        return (b1.BindingId, claim.Epoch);
    }

    private static string PausedCanonical(PausedInputPolicy input) =>
        new ModuleOfflinePolicy(AllOfflinePolicy.PauseAtBoundary, 30, 120, 1, input).Canonical;

    [Fact]
    public async Task PrepareDerivesPausedInputPolicyFromCanonicalFifthField()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var canonical = PausedCanonical(PausedInputPolicy.Compensate);

        var (p1, b1, _) = await MountAsync(identityStore, bindingStore, "live-key-pd");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-pd", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-pd", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-pd");
        await roomStore.PrepareRoundAsync("tenant-a", "game-a", "room-pd", "gs-1", claim.Epoch,
            [1, 2, 3], [4, 5, 6], 1, "prepare-pd", offlinePolicy: canonical);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT paused_input_policy,offline_policy FROM game.room WHERE room_id='room-pd'", connection);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Compensate", reader.GetString(0));
        Assert.Equal(canonical, reader.GetString(1));
    }

    [Fact]
    public async Task APausedBufferRoomRoutesInReceiveOrderAndDrainsInBudgetWindows()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);
        var frameStore = new RedesignFrameStore(database.ConnectionString);
        await PauseRoomAsync(identityStore, bindingStore, control, roomStore, "live-key-pb1",
            PausedCanonical(PausedInputPolicy.Buffer), "room-pb1", "gs-1");

        await eventStore.IngestAsync("simulator", "app-1", "batch-pb1",
        [
            new PlatformEventInput("gift-1", RedesignInputEventStore.Durable, "live-key-pb1",
                """{"kind":"gift","amount":1}"""),
            new PlatformEventInput("gift-2", RedesignInputEventStore.Durable, "live-key-pb1",
                """{"kind":"gift","amount":2}"""),
            new PlatformEventInput("gift-3", RedesignInputEventStore.Durable, "live-key-pb1",
                """{"kind":"gift","amount":3}""")
        ]);
        var routed = await inputStore.RouteEventsAsync("simulator", "app-1", "route-pb1", 50);
        Assert.False(routed.Duplicate);
        Assert.Equal(3, routed.Events.Count);
        Assert.All(routed.Events, item =>
        {
            Assert.Equal(RedesignRoundInputStore.Routed, item.Disposition);
            Assert.Equal("room-pb1", item.RoomId);
        });
        Assert.Equal([1L, 2L, 3L], routed.Events.Select(item => item.Sequence!.Value).OrderBy(s => s).ToArray());

        // A resumed actor drains under its per-tick budget: the bounded window returns only
        // the first two, and a full read orders every buffered delivery by the durable accept
        // order — never a reordering by a later platform arrival time.
        var bounded = await frameStore.ReadInboxAsync("tenant-a", "game-a", "room-pb1", 2);
        Assert.Equal(0L, bounded.Watermark);
        Assert.Equal([1L, 2L], bounded.Items.Select(item => item.Sequence).ToArray());
        var full = await frameStore.ReadInboxAsync("tenant-a", "game-a", "room-pb1", 500);
        Assert.Equal([1L, 2L, 3L], full.Items.Select(item => item.Sequence).ToArray());

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand("""
                SELECT count(*),min(sequence),max(sequence) FROM input.delivery
                WHERE consumer_scope='room:room-pb1' AND state='Pending'
                """, connection))
            await using (var reader = await deliveries.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(3L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(3L, reader.GetInt64(2));
            }
            // A paused room's buffer is durable and uncommitted: no frame ever consumed it.
            await using (var frames = new NpgsqlCommand(
                "SELECT count(*) FROM game.frame WHERE room_id='room-pb1'", connection))
                Assert.Equal(0L, Convert.ToInt64(await frames.ExecuteScalarAsync()));
            await using (var events = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE disposition='Routed' AND attributed_room_id='room-pb1'",
                connection))
                Assert.Equal(3L, Convert.ToInt64(await events.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task APausedBufferRoomOverflowIsObservableAndNeverAdvancesTheCursor()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);
        await PauseRoomAsync(identityStore, bindingStore, control, roomStore, "live-key-pb2",
            PausedCanonical(PausedInputPolicy.Buffer), "room-pb2", "gs-1");

        // Fill the bounded buffer to the cap inside one routing window.
        var toCap = Enumerable.Range(1, RedesignRoundInputStore.MaxPausedBufferPerRoom)
            .Select(i => new PlatformEventInput($"gift-{i}", RedesignInputEventStore.Durable,
                "live-key-pb2", $"{{\"kind\":\"gift\",\"amount\":{i}}}")).ToArray();
        await eventStore.IngestAsync("simulator", "app-1", "batch-cap", toCap);
        var filled = await inputStore.RouteEventsAsync("simulator", "app-1", "route-cap", 500);
        Assert.Equal(RedesignRoundInputStore.MaxPausedBufferPerRoom, filled.Events.Count);
        Assert.All(filled.Events, item => Assert.Equal(RedesignRoundInputStore.Routed, item.Disposition));
        Assert.Equal(1L, filled.Events.Select(item => item.Sequence!.Value).Min());
        Assert.Equal(RedesignRoundInputStore.MaxPausedBufferPerRoom,
            filled.Events.Select(item => item.Sequence!.Value).Max());

        // The next event overflows visibly: the room is attributed, no delivery is written,
        // and the cursor does not advance — no accepted event is dropped, none is silent.
        await eventStore.IngestAsync("simulator", "app-1", "batch-cap2",
        [
            new PlatformEventInput("gift-501", RedesignInputEventStore.Durable, "live-key-pb2",
                """{"kind":"gift","amount":501}""")
        ]);
        var overflow = await inputStore.RouteEventsAsync("simulator", "app-1", "route-cap2", 50);
        var item = Assert.Single(overflow.Events);
        Assert.Equal(RedesignRoundInputStore.PausedBufferFull, item.Disposition);
        Assert.Equal("room-pb2", item.RoomId);
        Assert.Null(item.Sequence);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE consumer_scope='room:room-pb2'", connection))
                Assert.Equal(500L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
            await using (var cursor = new NpgsqlCommand("""
                SELECT next_delivery_sequence FROM input.cursor
                WHERE consumer_scope='room:room-pb2'
                """, connection))
                Assert.Equal(501L, Convert.ToInt64(await cursor.ExecuteScalarAsync()));
            await using (var attributed = new NpgsqlCommand(
                "SELECT attributed_room_id FROM input.event WHERE source_dedup_key='gift-501'", connection))
                Assert.Equal("room-pb2", await attributed.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task APausedCompensateRoomDisposesOutOfRoundWithoutADelivery()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);
        await PauseRoomAsync(identityStore, bindingStore, control, roomStore, "live-key-pb3",
            PausedCanonical(PausedInputPolicy.Compensate), "room-pb3", "gs-1");

        await eventStore.IngestAsync("simulator", "app-1", "batch-pb3",
        [
            new PlatformEventInput("gift-c", RedesignInputEventStore.Durable, "live-key-pb3",
                """{"kind":"gift","amount":10}""")
        ]);
        var routed = await inputStore.RouteEventsAsync("simulator", "app-1", "route-pb3", 50);
        var item = Assert.Single(routed.Events);
        Assert.Equal(RedesignRoundInputStore.Compensated, item.Disposition);
        Assert.Equal("room-pb3", item.RoomId);
        Assert.Null(item.Sequence);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand("SELECT count(*) FROM input.delivery", connection))
                Assert.Equal(0L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
            await using (var cursors = new NpgsqlCommand("SELECT count(*) FROM input.cursor", connection))
                Assert.Equal(0L, Convert.ToInt64(await cursors.ExecuteScalarAsync()));
            await using (var marked = new NpgsqlCommand("""
                SELECT count(*) FROM input.event
                WHERE disposition='Compensated' AND attributed_room_id='room-pb3'
                """, connection))
                Assert.Equal(1L, Convert.ToInt64(await marked.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task APausedRejectRoomRefusesExplicitlyWithoutADelivery()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);
        await PauseRoomAsync(identityStore, bindingStore, control, roomStore, "live-key-pb4",
            PausedCanonical(PausedInputPolicy.Reject), "room-pb4", "gs-1");

        await eventStore.IngestAsync("simulator", "app-1", "batch-pb4",
        [
            new PlatformEventInput("gift-r", RedesignInputEventStore.Durable, "live-key-pb4",
                """{"kind":"gift","amount":20}""")
        ]);
        var routed = await inputStore.RouteEventsAsync("simulator", "app-1", "route-pb4", 50);
        var item = Assert.Single(routed.Events);
        Assert.Equal(RedesignRoundInputStore.PausedRejected, item.Disposition);
        Assert.Equal("room-pb4", item.RoomId);
        Assert.Null(item.Sequence);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand("SELECT count(*) FROM input.delivery", connection))
                Assert.Equal(0L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
            await using (var marked = new NpgsqlCommand("""
                SELECT count(*) FROM input.event
                WHERE disposition='PausedRejected' AND attributed_room_id='room-pb4'
                """, connection))
                Assert.Equal(1L, Convert.ToInt64(await marked.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task APausedRoomWithAnUndeclaredPolicyFailsClosed()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);
        // A fifth field the policy vocabulary does not name derives through prepare unchanged;
        // the gate refuses it closed — never a silent accept, never a silent drop.
        await PauseRoomAsync(identityStore, bindingStore, control, roomStore, "live-key-pb5",
            "PauseAtBoundary|30|120|1|Mystery", "room-pb5", "gs-1");

        await eventStore.IngestAsync("simulator", "app-1", "batch-pb5",
        [
            new PlatformEventInput("gift-u", RedesignInputEventStore.Durable, "live-key-pb5",
                """{"kind":"gift","amount":30}""")
        ]);
        var routed = await inputStore.RouteEventsAsync("simulator", "app-1", "route-pb5", 50);
        var item = Assert.Single(routed.Events);
        Assert.Equal(RedesignRoundInputStore.PausedPolicyUnset, item.Disposition);
        Assert.Equal("room-pb5", item.RoomId);
        Assert.Null(item.Sequence);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var deliveries = new NpgsqlCommand("SELECT count(*) FROM input.delivery", connection);
        Assert.Equal(0L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task APausedBufferCountsPrePausePendingAndReplaysAnIngestDuplicate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        // Three accepted events while the room is live, then the pause: the three Pending
        // deliveries were accepted before the boundary and must count toward the cap.
        var (p1, b1, _) = await MountAsync(identityStore, bindingStore, "live-key-pb6");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-pb6", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-pb6", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-pb6");
        await roomStore.PrepareRoundAsync("tenant-a", "game-a", "room-pb6", "gs-1", claim.Epoch,
            [1, 2, 3], [4, 5, 6], 1, "prepare-pb6",
            offlinePolicy: PausedCanonical(PausedInputPolicy.Buffer));
        await roomStore.ActivateRoundAsync("tenant-a", "game-a", "room-pb6", "gs-1", claim.Epoch,
            "activate-pb6");
        await eventStore.IngestAsync("simulator", "app-1", "batch-pre",
        [
            new PlatformEventInput("gift-1", RedesignInputEventStore.Durable, "live-key-pb6",
                """{"kind":"gift","amount":1}"""),
            new PlatformEventInput("gift-2", RedesignInputEventStore.Durable, "live-key-pb6",
                """{"kind":"gift","amount":2}"""),
            new PlatformEventInput("gift-3", RedesignInputEventStore.Durable, "live-key-pb6",
                """{"kind":"gift","amount":3}""")
        ]);
        var live = await inputStore.RouteEventsAsync("simulator", "app-1", "route-pre", 50);
        Assert.All(live.Events, item => Assert.Equal(RedesignRoundInputStore.Routed, item.Disposition));
        await roomStore.PauseRoundAsync("tenant-a", "game-a", "room-pb6", "gs-1", claim.Epoch,
            "all control sessions offline", "pause-pb6", 0, 1);

        // A platform retry of an accepted event is a duplicate with the original attribution.
        var redelivered = await eventStore.IngestAsync("simulator", "app-1", "batch-dup",
        [
            new PlatformEventInput("gift-1", RedesignInputEventStore.Durable, "live-key-pb6",
                """{"kind":"gift","amount":1}""")
        ]);
        Assert.True(redelivered.Receipts[0].Duplicate);
        Assert.Equal("room-pb6", redelivered.Receipts[0].RoomId);
        // The already-routed event never re-enters a routing window.
        var again = await inputStore.RouteEventsAsync("simulator", "app-1", "route-dup", 50);
        Assert.Empty(again.Events);

        // 3 pre-pause pendings + 497 more fill the 500 cap exactly; the next new event
        // overflows — without the pre-pause count it would have fit.
        var nearCap = Enumerable.Range(1, 497)
            .Select(i => new PlatformEventInput($"gift-extra-{i}", RedesignInputEventStore.Durable,
                "live-key-pb6", $"{{\"kind\":\"gift\",\"amount\":{i + 1000}}}")).ToArray();
        await eventStore.IngestAsync("simulator", "app-1", "batch-extra", nearCap);
        var near = await inputStore.RouteEventsAsync("simulator", "app-1", "route-extra", 500);
        Assert.Equal(497, near.Events.Count);
        Assert.All(near.Events, item => Assert.Equal(RedesignRoundInputStore.Routed, item.Disposition));
        await eventStore.IngestAsync("simulator", "app-1", "batch-extra2",
        [
            new PlatformEventInput("gift-501", RedesignInputEventStore.Durable, "live-key-pb6",
                """{"kind":"gift","amount":999}""")
        ]);
        var overflow = await inputStore.RouteEventsAsync("simulator", "app-1", "route-extra2", 50);
        Assert.Equal(RedesignRoundInputStore.PausedBufferFull, Assert.Single(overflow.Events).Disposition);

        // The duplicate never wrote a second delivery row: one accepted event, one delivery.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var gift1 = new NpgsqlCommand("""
                SELECT count(*) FROM input.delivery d
                JOIN input.event e ON e.event_id=d.event_id
                WHERE e.source_dedup_key='gift-1'
                """, connection))
                Assert.Equal(1L, Convert.ToInt64(await gift1.ExecuteScalarAsync()));
            await using (var total = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE consumer_scope='room:room-pb6'", connection))
                Assert.Equal(500L, Convert.ToInt64(await total.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task AStandingEndOperationFencesAPausedBufferRoomBeforeItBuffers()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);
        await PauseRoomAsync(identityStore, bindingStore, control, roomStore, "live-key-pb8",
            PausedCanonical(PausedInputPolicy.Buffer), "room-pb8", "gs-1");

        // A standing end operation closes the paused room's input generation (FIX-END-06):
        // the fence outranks the buffer — the late arrival is terminal, never a delivery.
        await EndLadderEvidence.StandAsync(database.ConnectionString, "room-pb8");
        await eventStore.IngestAsync("simulator", "app-1", "batch-pb8",
        [
            new PlatformEventInput("gift-late", RedesignInputEventStore.Durable, "live-key-pb8",
                """{"kind":"gift","amount":5}""")
        ]);
        var routed = await inputStore.RouteEventsAsync("simulator", "app-1", "route-pb8", 50);
        var item = Assert.Single(routed.Events);
        Assert.Equal(RedesignRoundInputStore.OutOfRound, item.Disposition);
        Assert.Equal("room-pb8", item.RoomId);
        Assert.Null(item.Sequence);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var deliveries = new NpgsqlCommand("SELECT count(*) FROM input.delivery", connection);
        Assert.Equal(0L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
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

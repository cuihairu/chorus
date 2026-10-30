using System.Text.Json;
using Chorus.Rpc;
using Chorus.StreamerGateway.Messages;

namespace Chorus.StreamerGateway;

/// <summary>
/// The room fan-out (architecture 8.3) as the edge owns it: one RoundSync subscription per
/// room, shared by every connection the match put into it. The subscription's baseline is the
/// seat that joins, so a seat arriving mid-round — or a connection whose outbox overflowed —
/// re-baselines the room with a fresh snapshot, and the per-connection outbox turns that into
/// a clean supersession instead of a broken chain. The gateway holds no room state of its
/// own: every frame is the actor's committed record, passed through in the client's
/// vocabulary. A room that moves epochs re-subscribes at the new one; a room that parks
/// itself surfaces as roomSuspended; a stream that ends without a result tells the client to
/// resynchronize. Ordering is structural: a seat's roomJoined is sent on the serialized
/// downlink lane and only then is the frame queued behind it, so the client can never see a
/// frame before the room it belongs to.
/// </summary>
/// <summary>One subscription's ask: the room, where its host serves, the epoch the frames
/// must carry, and the applied baseline the stream continues from.</summary>
public sealed record RoomStreamRequest(
    string RoomId, string GameId, string Endpoint, long Epoch,
    long LastAppliedTick, long LastAppliedStateVersion);

public sealed class EdgeRoomFanout(Func<RoomStreamRequest, CancellationToken, IAsyncEnumerable<SyncMessage>> subscribe)
{
    /// <summary>What the edge re-resolves when a stream ends: where the room lives now and in
    /// which status — the composition of the routing watch and the round store.</summary>
    public sealed record ResolvedRoom(string GameId, string Endpoint, long Epoch, string Status);

    private sealed class Seat(EdgeConnection connection, string participation)
    {
        public EdgeConnection Connection { get; } = connection;
        /// <summary>The side this connection plays — the binding's role in the round.</summary>
        public string Participation { get; } = participation;
        /// <summary>False until the seat's first frame carried its roomJoined.</summary>
        public bool JoinedSent { get; set; }
    }

    private sealed class RoomState(string roomId)
    {
        public string RoomId { get; } = roomId;
        public string GameId { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public long Epoch { get; set; }
        public Dictionary<string, Seat> Seats { get; } = new(StringComparer.Ordinal);
        /// <summary>The stream head as last seen — the baseline a joining seat compares to.</summary>
        public long LastTick { get; set; }
        public long LastVersion { get; set; }
        /// <summary>Module facts from the last frame — a deltas-only seat's roomJoined source.</summary>
        public string ModuleVersion { get; set; } = "";
        public string ConfigHash { get; set; } = "";
        /// <summary>Set when the actor's round result was handed to the seats: a stream that
        /// completes after it ended the round well, ends the room quietly.</summary>
        public bool ResultDelivered { get; set; }
        /// <summary>Broken-stream retries this room has left; every delivered frame resets it.</summary>
        public int StreamFailures { get; set; }
        /// <summary>Bumped on every (re)subscription; a reader only finalizes its own
        /// generation, so a superseded stream cannot tear down its replacement.</summary>
        public long Generation { get; set; }
        /// <summary>The stream of this room has run to its end. Cancel, never dispose: the
        /// superseded reader may still hold the old token.</summary>
        public CancellationTokenSource StreamCancellation { get; set; } = new();
        /// <summary>Commands this room's seats sent and the store accepted, awaiting the delta
        /// that consumes them — command id → the connection that sent it.</summary>
        public Dictionary<string, string> PendingCommands { get; } = new(StringComparer.Ordinal);
        /// <summary>Serializes frame dispatch per room: the roomJoined send must complete
        /// before the frame queues behind it, and two frames must not interleave mid-room.</summary>
        public SemaphoreSlim Dispatch { get; } = new(1, 1);
    }

    /// <summary>The pending-command bound per room: a client flooding commands the round never
    /// consumes must not grow the room's tracking set without end.</summary>
    private const int PendingCommandCapacity = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, RoomState> _rooms = new(StringComparer.Ordinal);

    /// <summary>Fires exactly once per subscription that was handed a stream — the reader's
    /// finally, or the failure of the subscribe call itself. The production channel pool
    /// refcounts game-host channels on it; tests leave it null.</summary>
    public Action<string>? StreamClosed { get; init; }

    /// <summary>Re-resolves a room after its stream ended — routing facts plus the round
    /// status, or null when the room is nowhere. Production composes the routing watch and
    /// the round store; tests drive it directly.</summary>
    public required Func<string, CancellationToken, Task<ResolvedRoom?>> ResolveRoom { get; init; }

    /// <summary>Seats one connection in its match's room. The facts come from the caller's
    /// own resolution (routing endpoint, round epoch, the binding's participation); the
    /// baseline comes from what the connection last acknowledged on this room. A seat at the
    /// stream's head rides the deltas alone; anything older re-baselines the room so the
    /// seat starts from a snapshot it can apply.</summary>
    public void Join(string roomId, string gameId, string endpoint, long epoch,
        string participation, EdgeConnection connection)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
            {
                room = new RoomState(roomId);
                _rooms.Add(roomId, room);
            }
            if (room.Generation > 0 && epoch != room.Epoch)
            {
                // The room moved between the caller's resolve and now: the seats re-hear the
                // room (and its new epoch) when the re-subscribed snapshot arrives.
                foreach (var seat in room.Seats.Values) seat.JoinedSent = false;
            }
            room.GameId = gameId;
            room.Endpoint = endpoint;
            room.Epoch = epoch;
            room.Seats[connection.ConnectionId] = new Seat(connection, participation);
            var baselineTick = 0L;
            var baselineVersion = 0L;
            if (string.Equals(connection.AppliedRoomId, roomId, StringComparison.Ordinal))
            {
                baselineTick = connection.AppliedTick;
                baselineVersion = connection.AppliedStateVersion;
            }
            if (room.Generation == 0
                || baselineTick != room.LastTick || baselineVersion != room.LastVersion)
            {
                StartStream(room, baselineTick, baselineVersion);
            }
        }
    }

    /// <summary>Removes one connection's seat; the last seat out ends the room's stream.</summary>
    public void Leave(string connectionId)
    {
        lock (_gate)
        {
            foreach (var room in _rooms.Values)
            {
                if (!room.Seats.Remove(connectionId)) continue;
                DropPendingCommands(room, connectionId);
                if (room.Seats.Count == 0) RemoveRoom(room);
                break;
            }
        }
    }

    /// <summary>Registers a command the store accepted for a seat, so the delta that consumes
    /// it can route the Applied receipt to the connection that sent it. False when the room is
    /// gone or the connection holds no seat in it — the command raced the room's end or the
    /// join — or the room's tracking set is full; in every case the command itself is the
    /// store's to judge, only the receipt's routing is best-effort.</summary>
    public bool TrackCommand(string roomId, string commandId, string connectionId)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return false;
            if (!room.Seats.ContainsKey(connectionId)) return false;
            if (room.PendingCommands.Count >= PendingCommandCapacity
                && !room.PendingCommands.ContainsKey(commandId))
                return false;
            room.PendingCommands[commandId] = connectionId;
            return true;
        }
    }

    /// <summary>Drops a pending registration — a command the store refused has no Applied leg.</summary>
    public void UntrackCommand(string roomId, string commandId)
    {
        lock (_gate)
        {
            if (_rooms.TryGetValue(roomId, out var room)) room.PendingCommands.Remove(commandId);
        }
    }

    private static void DropPendingCommands(RoomState room, string connectionId)
    {
        // The caller holds the gate.
        if (room.PendingCommands.Count == 0) return;
        foreach (var command in room.PendingCommands
            .Where(pair => pair.Value == connectionId).Select(pair => pair.Key).ToList())
            room.PendingCommands.Remove(command);
    }

    /// <summary>Re-baselines a room from zero: the actor answers with the head snapshot and
    /// every seat's outbox supersedes its chain. False when the connection holds no seat in
    /// the room — or the room is already gone.</summary>
    public bool RequestSnapshot(string connectionId, string roomId)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return false;
            if (!room.Seats.ContainsKey(connectionId)) return false;
            StartStream(room, 0, 0);
            return true;
        }
    }

    private void RemoveRoom(RoomState room)
    {
        // The caller holds the gate.
        if (!_rooms.TryGetValue(room.RoomId, out var current) || !ReferenceEquals(current, room))
            return;
        _rooms.Remove(room.RoomId);
        room.StreamCancellation.Cancel();
    }

    /// <summary>Starts (or replaces) the room's subscription at a baseline. The caller holds
    /// the gate; the reader runs outside it and re-enters per dispatch.</summary>
    private void StartStream(RoomState room, long baselineTick, long baselineVersion)
    {
        room.Generation++;
        var generation = room.Generation;
        room.StreamCancellation.Cancel();
        room.StreamCancellation = new CancellationTokenSource();
        var cancellation = room.StreamCancellation.Token;
        var request = new RoomStreamRequest(room.RoomId, room.GameId, room.Endpoint, room.Epoch,
            baselineTick, baselineVersion);
        IAsyncEnumerable<SyncMessage> stream;
        try
        {
            stream = subscribe(request, cancellation);
        }
        catch
        {
            // The subscription never opened: the release the reader's finally would have
            // owned happens here.
            StreamClosed?.Invoke(room.Endpoint);
            throw;
        }
        _ = Task.Run(() => ReadAsync(room, generation, stream, cancellation));
    }

    private async Task ReadAsync(RoomState room, long generation,
        IAsyncEnumerable<SyncMessage> stream, CancellationToken cancellationToken)
    {
        var endpoint = room.Endpoint;
        try
        {
            await foreach (var message in stream.WithCancellation(cancellationToken))
            {
                lock (_gate)
                {
                    if (generation != room.Generation) return;
                }
                // The actor's resync is a verdict, not a frame: the resolve below decides
                // whether the room moved, parked, or ended.
                if (message.KindCase == SyncMessage.KindOneofCase.ResyncRequired) break;
                await DispatchAsync(room, message);
            }
            if (room.ResultDelivered)
            {
                // The round ended well: the result is in every seat's outbox and the actor
                // completed the stream. The room is done; the seats stay for the next match.
                lock (_gate) RemoveRoom(room);
                return;
            }
            await ResolveOrEndAsync(room, generation,
                "The room stopped before committing a round result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Replaced or torn down — its replacement (or the removal) owns the room now.
        }
        catch (Exception)
        {
            // gRPC surfaces a cancelled stream as an RpcException; that is a replacement,
            // not a failure. Everything else resolves: one retry when the room is still
            // live, an honest resync when it is not.
            if (cancellationToken.IsCancellationRequested) return;
            await ResolveOrEndAsync(room, generation,
                "The room stream ended unexpectedly.", retryFailures: true);
        }
        finally
        {
            StreamClosed?.Invoke(endpoint);
        }
    }

    /// <summary>The stream ended without a result: re-resolve the room and follow the facts —
    /// a parked room is roomSuspended, a moved room re-subscribes at its current epoch, a
    /// live room retries once after a broken stream, and anything else resynchronizes the
    /// seats honestly instead of freezing them on a dead chain.</summary>
    private async Task ResolveOrEndAsync(RoomState room, long generation, string reason,
        bool retryFailures = false)
    {
        ResolvedRoom? resolved = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            resolved = await ResolveRoom(room.RoomId, timeout.Token);
        }
        catch
        {
            // Resolution is best-effort; the fallback below is the honest resync.
        }
        List<Seat> seats;
        lock (_gate)
        {
            if (generation != room.Generation) return;
            if (resolved is { Status: not "Suspended" } live
                && (live.Epoch != room.Epoch
                    || (retryFailures && room.StreamFailures < 3)))
            {
                // Same-epoch retries count against the budget; an epoch move always follows.
                if (live.Epoch == room.Epoch) room.StreamFailures++;
                room.GameId = live.GameId;
                room.Endpoint = live.Endpoint;
                room.Epoch = live.Epoch;
                foreach (var seat in room.Seats.Values) seat.JoinedSent = false;
                StartStream(room, 0, 0);
                return;
            }
            seats = room.Seats.Values.ToList();
        }
        foreach (var seat in seats)
        {
            if (resolved is { Status: "Suspended" })
            {
                await TrySendAsync(seat.Connection, new EdgeRoomSuspended
                {
                    Reason = reason,
                    RecoveryAttempt = resolved.Epoch
                });
            }
            else
            {
                await TrySendAsync(seat.Connection, new EdgeResyncRequired
                {
                    RoomId = room.RoomId, RoomEpoch = room.Epoch, Reason = reason
                });
            }
        }
        lock (_gate) RemoveRoom(room);
    }

    /// <summary>Applies one committed message to every seat, behind the room's dispatch gate:
    /// the roomJoined send completes before the frame queues behind it, so a seat never sees
    /// a frame for a room it has not been told it is in.</summary>
    private async Task DispatchAsync(RoomState room, SyncMessage message)
    {
        await room.Dispatch.WaitAsync(CancellationToken.None);
        try
        {
            switch (message.KindCase)
            {
                case SyncMessage.KindOneofCase.Snapshot:
                {
                    var snapshot = message.Snapshot;
                    lock (_gate)
                    {
                        room.LastTick = snapshot.Tick;
                        room.LastVersion = snapshot.StateVersion;
                        room.ModuleVersion = snapshot.ModuleVersion;
                        room.ConfigHash = snapshot.ConfigHash;
                        room.StreamFailures = 0;
                    }
                    var frame = JsonSerializer.Serialize(new EdgeSnapshot
                    {
                        RoomId = snapshot.RoomId, RoomEpoch = snapshot.Epoch, TickId = snapshot.Tick,
                        StateVersion = snapshot.StateVersion, SchemaVersion = snapshot.StateSchemaVersion,
                        Payload = snapshot.StatePayload.ToArray()
                    }, EdgeJson.Options);
                    foreach (var seat in SeatsOf(room))
                    {
                        if (!await SendRoomJoinedAsync(room, seat, snapshot.Epoch)) continue;
                        seat.Connection.Outbox.EnqueueSnapshot(frame);
                    }
                    break;
                }
                case SyncMessage.KindOneofCase.Delta:
                {
                    var delta = message.Delta;
                    lock (_gate)
                    {
                        room.LastTick = delta.Tick;
                        room.LastVersion = delta.StateVersion;
                        room.ModuleVersion = delta.ModuleVersion;
                        room.ConfigHash = delta.ConfigHash;
                        room.StreamFailures = 0;
                    }
                    var frame = JsonSerializer.Serialize(new EdgeDelta
                    {
                        RoomId = delta.RoomId, RoomEpoch = delta.Epoch, TickId = delta.Tick,
                        BaseVersion = delta.BaseVersion, StateVersion = delta.StateVersion,
                        Payload = EffectsPayload(delta)
                    }, EdgeJson.Options);
                    foreach (var seat in SeatsOf(room))
                    {
                        if (!await SendRoomJoinedAsync(room, seat, delta.Epoch)) continue;
                        // A refused delta means the ordered queue is full: only a snapshot
                        // re-baselines it, and the room re-subscription below delivers one.
                        seat.Connection.Outbox.TryEnqueueOrdered(frame);
                    }
                    // The delta that consumes a command is the applied word (contracts 3.2):
                    // each receipt follows the delta that carries it, on the ordered lane, to
                    // exactly the seat that sent the command.
                    foreach (var command in delta.Commands)
                    {
                        string? ownerId;
                        lock (_gate)
                        {
                            if (!room.PendingCommands.Remove(command.CommandId, out ownerId))
                                continue;
                        }
                        var owner = SeatsOf(room).FirstOrDefault(
                            seat => seat.Connection.ConnectionId == ownerId);
                        if (owner is null) continue;
                        owner.Connection.Outbox.TryEnqueueOrdered(JsonSerializer.Serialize(
                            new EdgeCommandReceipt
                            {
                                CommandId = command.CommandId, Status = "Applied",
                                ReceiptId = $"{room.RoomId}:{command.Sequence}"
                            }, EdgeJson.Options));
                    }
                    lock (_gate)
                    {
                        foreach (var seat in room.Seats.Values)
                            if (seat.Connection.Outbox.NeedsSnapshot)
                            {
                                StartStream(room, 0, 0);
                                break;
                            }
                    }
                    break;
                }
                case SyncMessage.KindOneofCase.RoundResult:
                {
                    var result = message.RoundResult;
                    lock (_gate)
                    {
                        room.ResultDelivered = true;
                        room.LastTick = result.Tick;
                        room.LastVersion = result.StateVersion;
                        room.StreamFailures = 0;
                    }
                    using var document = JsonDocument.Parse(result.ResultJson);
                    var frame = JsonSerializer.Serialize(new EdgeRoundResult
                    {
                        ResultId = $"{room.RoomId}:{result.Epoch}:{result.Tick}",
                        SettlementState = "Committed",
                        Result = document.RootElement.Clone()
                    }, EdgeJson.Options);
                    foreach (var seat in SeatsOf(room))
                    {
                        if (!await SendRoomJoinedAsync(room, seat, result.Epoch)) continue;
                        // Sticky: the result is the receipt the client must see — it is never
                        // dropped and it survives a later snapshot's supersession.
                        seat.Connection.Outbox.TryEnqueueOrdered(frame, sticky: true);
                    }
                    break;
                }
            }
        }
        finally
        {
            room.Dispatch.Release();
        }
    }

    private List<Seat> SeatsOf(RoomState room)
    {
        lock (_gate) return room.Seats.Values.ToList();
    }

    /// <summary>The seat's first frame carries its roomJoined: the room identity, its side,
    /// and the module every frame on this chain will name. False when the connection is
    /// gone — the frame behind it has nowhere to go either.</summary>
    private async Task<bool> SendRoomJoinedAsync(RoomState room, Seat seat, long epoch)
    {
        if (seat.JoinedSent) return true;
        seat.JoinedSent = true;
        return await TrySendAsync(seat.Connection, new EdgeRoomJoined
        {
            RoomId = room.RoomId, RoomEpoch = epoch, Participation = seat.Participation,
            ModuleVersion = room.ModuleVersion, ConfigHash = room.ConfigHash
        });
    }

    /// <summary>A direct downlink that must not throw: a dead connection's cleanup belongs
    /// to its dispatch loop, not to the room's reader.</summary>
    private static async Task<bool> TrySendAsync(EdgeConnection connection, object message)
    {
        try
        {
            await connection.SendJsonAsync(message, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The delta payload is the frame's effects as a JSON array — the module's own
    /// delta vocabulary (kind plus its parsed payload), not an edge invention.</summary>
    private static JsonElement EffectsPayload(DeltaMessage delta)
    {
        if (delta.Effects.Count == 0)
            return JsonDocument.Parse("[]").RootElement.Clone();
        var effects = new System.Text.Json.Nodes.JsonArray();
        foreach (var effect in delta.Effects)
            effects.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = effect.Kind,
                ["payload"] = string.IsNullOrEmpty(effect.PayloadJson)
                    ? null
                    : System.Text.Json.Nodes.JsonNode.Parse(effect.PayloadJson)
            });
        return JsonDocument.Parse(effects.ToJsonString()).RootElement.Clone();
    }
}

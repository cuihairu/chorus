using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.GameServer;

public sealed partial class RoomRuntime
{
    /// <summary>
    /// FIX-INPUT-01: the direct-push room-event edge (architecture 6.3). The caller names
    /// the owner instance and epoch it believes current; a host that does not hold the room
    /// at that exact picture answers StaleOwner without touching the data plane. The durable
    /// leg runs on the data plane, where the ownership row fences the push again under the
    /// store's lock — so DurableAccepted and Duplicate always mean committed, never applied.
    /// A saturated or closed inbound bound answers Overloaded before any durable write; a
    /// data-plane call whose outcome the host cannot know (deadline passed, store
    /// unreachable) answers OutcomeUnknown — the caller retries the same event id and the
    /// stored receipt decides between Duplicate and DurableAccepted.
    /// FIX-INPUT-03: the room's own inbound seat list joins the capacity check, and a
    /// committed event is handed to the owning actor and wakes it — the notification only
    /// wakes, the recoverable inbox cursor stays the consumption authority, and a seat
    /// taken in memory never confirms an event by itself.
    /// </summary>
    public async Task<DeliverRoomEventReply> DeliverEventAsync(DeliverRoomEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TenantId.Length == 0 || request.GameId.Length == 0 || request.RoomId.Length == 0 ||
            request.OwnerInstanceId.Length == 0 || request.EventId.Length == 0 ||
            request.Platform.Length == 0 || request.AppId.Length == 0 || request.PayloadJson.Length == 0)
            throw new ArgumentException(
                "tenant_id, game_id, room_id, owner_instance_id, event_id, platform, app_id and payload_json are required.");
        if (request.Epoch <= 0)
            throw new ArgumentException("The room epoch must be positive.");
        // FIX-END-06: an attributed push states the round generation its routing snapshot
        // claimed for the binding; the data plane checks the claim against the binding row,
        // so a stale snapshot's push for a closed round is refused at admission.
        if (request.BindingId.Length > 0 && request.RoundGeneration <= 0)
            throw new ArgumentException(
                "An attributed push must name the round generation its route snapshot claimed.");
        try { using (JsonDocument.Parse(request.PayloadJson)) { } }
        catch (JsonException error) { throw new ArgumentException("The payload is not valid JSON.", error); }

        var actor = FindRoom(request.RoomId);
        if (actor is null || actor.OwnerInstanceId != request.OwnerInstanceId ||
            actor.Epoch != request.Epoch || actor.GameId != request.GameId)
            return new DeliverRoomEventReply
            {
                Disposition = StaleOwner,
                Note = "This host does not own the room at the named owner instance and epoch."
            };
        // FIX-SYNC-07: a paused actor is mid-boundary (PauseAtBoundary sets Paused before the
        // up-to-10s PauseRoomAsync await, and the actor only retires on loop end) — answering
        // StaleOwner sends the push to the gateway's durable ingest leg instead of the store's
        // paused admission refusal, which the gateway would treat as terminal and the event
        // would be lost. The paused room's policy is enforced by the ingest-side gate.
        if (actor.Status().State == RoomRunState.Paused)
            return new DeliverRoomEventReply
            {
                Disposition = StaleOwner,
                Note = "The room is paused; its durable ingest leg handles paused inputs."
            };
        // FIX-INPUT-03: capacity precedes persistence (architecture 6.1 step 4) — a room
        // whose seat list is full answers Overloaded before any durable write, so a refused
        // push commits nothing. An event id that already holds a seat always passes: an
        // idempotent replay never blocks on capacity.
        if (!actor.HasInboundSeat(request.EventId))
            return new DeliverRoomEventReply
            {
                Disposition = Overloaded,
                Note = "The room's inbound seat list is full."
            };
        if (_roomEventGate is null || !_roomEventGate.Wait(0))
            return new DeliverRoomEventReply
            {
                Disposition = Overloaded,
                Note = "The host's inbound room-event bound is closed or saturated."
            };
        if (_dataChannel is null)
        {
            _roomEventGate.Release();
            throw new InvalidOperationException("The data-plane channel is not configured.");
        }
        try
        {
            // FIX-LINK-09: a @chorus push is a client command — its durable leg is the
            // session/participation/rate-cap AcceptClientInput path (the authoritative
            // fence), not the room-attributed AcceptRoomEvent path; both answer the same
            // duplicate/sequence/route-version shape and hand the seat identically. The
            // @chorus leg's seat references the delivery row the store wrote (its own event
            // id) while surfacing the client's command id as the Applied word.
            bool duplicate;
            long sequence;
            long routeVersion;
            string seatEventId;
            string seatDedupKey;
            if (request.Platform == ClientCommandPlatform)
            {
                var accepted = await new RoundInput.RoundInputClient(_dataChannel)
                    .AcceptClientInputAsync(new ClientInputRequest
                    {
                        TenantId = request.TenantId, SessionId = request.SessionId,
                        RoomId = request.RoomId, RoomEpoch = request.Epoch,
                        GameId = request.GameId, CommandId = request.EventId,
                        PayloadJson = request.PayloadJson
                    }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
                duplicate = accepted.Duplicate;
                sequence = accepted.Sequence;
                routeVersion = accepted.RouteVersion;
                seatEventId = accepted.EventId;
                seatDedupKey = request.EventId;
            }
            else
            {
                var accepted = await new RoundInput.RoundInputClient(_dataChannel)
                    .AcceptRoomEventAsync(new AcceptRoomEventRequest
                    {
                        TenantId = request.TenantId, GameId = request.GameId, RoomId = request.RoomId,
                        OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
                        EventId = request.EventId, Platform = request.Platform, AppId = request.AppId,
                        BindingId = request.BindingId, RoundGeneration = request.RoundGeneration,
                        PayloadJson = request.PayloadJson
                    }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
                duplicate = accepted.Duplicate;
                sequence = accepted.Sequence;
                routeVersion = accepted.RouteVersion;
                seatEventId = request.EventId;
                seatDedupKey = request.EventId;
            }
            // FIX-INPUT-03: the handoff follows the durable verdict — an in-memory seat
            // never confirms anything by itself. Only a fresh accept hands: a duplicate's
            // event may already be consumed, and re-seating a drained id would send the
            // tick at a delivery the frame commit must refuse. An accept whose hand loses
            // a capacity race, or whose reply was lost (OutcomeUnknown), costs nothing —
            // the delivery row is the truth and the cursor pull consumes it; the wake
            // still fires either way.
            if (!duplicate)
            {
                // FIX-OBS-11: the session face's own decision — a seated hand is the event
                // ingested, a refused seat is the face dropping it (the durable delivery row
                // stays the truth either way; the pull retries what the seat refused).
                if (actor.TryHandWithSource(seatEventId, seatDedupKey,
                        request.BindingId is { Length: > 0 } ? request.BindingId : null,
                        request.PayloadJson, request.Platform, sequence))
                    EventCounters.Ingested();
                else
                    EventCounters.Dropped();
            }
            actor.Wake();
            return new DeliverRoomEventReply
            {
                Disposition = duplicate ? Duplicate : DurableAccepted,
                Sequence = sequence, RouteVersion = routeVersion
            };
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            return new DeliverRoomEventReply { Disposition = StaleOwner, Note = error.Status.Detail };
        }
        catch (RpcException error) when (error.StatusCode is StatusCode.DeadlineExceeded
            or StatusCode.Unavailable)
        {
            return new DeliverRoomEventReply
            {
                Disposition = OutcomeUnknown,
                Note = "The durable leg's outcome is unknown; retry the same event id."
            };
        }
        finally
        {
            _roomEventGate.Release();
        }
    }

    /// <summary>
    /// FIX-GM-07: the operator-delegated playtest command (architecture 12.1's room command
    /// row). The ControlServer authenticated the operator and routes here through its
    /// directory; this host is the target-version authority — it answers StaleOwner unless it
    /// owns the room — and resolves the named participant's live binding itself, never a
    /// caller-named endpoint or binding. The command then rides exactly the room-attributed
    /// push path a platform event rides (durable accept under the operation id, seat, wake),
    /// so it takes effect at a committed tick and a lost reply is a verbatim retry that the
    /// store's receipt answers as Duplicate. The "gm" platform keeps the payload raw for the
    /// module — the module's own command rule is the trust boundary that rejects shapes it
    /// does not know, exactly as it does for a client's intent.
    /// </summary>
    public async Task<DeliverRoomCommandReply> DeliverRoomCommandAsync(DeliverRoomCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TenantId.Length == 0 || request.GameId.Length == 0 || request.RoomId.Length == 0 ||
            request.OperationId.Length == 0 || request.ParticipantId.Length == 0 ||
            request.PayloadJson.Length == 0)
            throw new ArgumentException(
                "tenant_id, game_id, room_id, operation_id, participant_id and payload_json are required.");
        try { using (JsonDocument.Parse(request.PayloadJson)) { } }
        catch (JsonException error) { throw new ArgumentException("The payload is not valid JSON.", error); }

        var actor = FindRoom(request.RoomId);
        if (actor is null || actor.GameId != request.GameId)
            return new DeliverRoomCommandReply
            {
                OperationId = request.OperationId, Disposition = StaleOwner,
                Note = "This host does not run this room."
            };
        var binding = actor.BindingForParticipant(request.ParticipantId);
        if (binding is null)
            return new DeliverRoomCommandReply
            {
                OperationId = request.OperationId, Disposition = UnknownParticipant,
                OwnerInstanceId = actor.OwnerInstanceId, Epoch = actor.Epoch,
                Note = "The named participant holds no live seat on this room."
            };
        if (_dataChannel is null)
            throw new InvalidOperationException("The data-plane channel is not configured.");
        // The binding read is the dispatch's own admission claim: the seat must still point
        // at this room, and the generation the store confirms is what AcceptRoomEvent's
        // fence re-checks under lock. A seat closed between the two reads refuses there —
        // the stale claim is the refusal, never a misplaced command.
        var seat = await new RoundInput.RoundInputClient(_dataChannel).GetBindingAsync(
            new GetBindingRequest { BindingId = binding },
            deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        if (!seat.Found || seat.CurrentRoomId != request.RoomId || seat.RoundGeneration <= 0)
            return new DeliverRoomCommandReply
            {
                OperationId = request.OperationId, Disposition = UnknownParticipant,
                OwnerInstanceId = actor.OwnerInstanceId, Epoch = actor.Epoch,
                Note = "The participant's binding no longer points at this room."
            };
        var delivered = await DeliverEventAsync(new DeliverRoomEventRequest
        {
            TenantId = request.TenantId, GameId = request.GameId, RoomId = request.RoomId,
            OwnerInstanceId = actor.OwnerInstanceId, Epoch = actor.Epoch,
            EventId = request.OperationId, Platform = RoomCommandPlatforms.Gm,
            AppId = RoomCommandPlatforms.GmAppId, BindingId = binding,
            RoundGeneration = seat.RoundGeneration, PayloadJson = request.PayloadJson
        }, cancellationToken);
        return new DeliverRoomCommandReply
        {
            OperationId = request.OperationId, Disposition = delivered.Disposition,
            OwnerInstanceId = actor.OwnerInstanceId, Epoch = actor.Epoch, Note = delivered.Note
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var actor in _actors.Values)
        {
            await actor.DisposeAsync();
            // FIX-PERF-07: every actor the host held reserved one slot; a live room's teardown
            // retires it (releasing there), a finished room stays in the dictionary until this
            // removal — the gate keeps the free exactly once per reservation.
            if (_actors.TryRemove(new KeyValuePair<string, RoomActor>(actor.RoomId, actor)))
                _admission.Release();
        }
        _actors.Clear();
        _dataChannel?.Dispose();
    }

    /// <summary>The host's measured load (FIX-PERF-07): rooms whose commits blow the tick
    /// budget and are observably Degraded (FIX-PERF-06). This is the load signal a new
    /// allocation is budgeted against — an honest count of demonstrably overloaded rooms,
    /// not an estimate.</summary>
    private int DegradedRoomCount()
    {
        var count = 0;
        foreach (var actor in _actors.Values)
            if (actor.Status().State == RoomRunState.Degraded) count++;
        return count;
    }

    /// <summary>The admission word this host reports to the control plane (FIX-SCHED-02):
    /// <c>Full</c> when its own budget would refuse a new room, <c>Accepting</c> otherwise.
    /// The scheduler filters on it, so a capacity-bound host stops being offered rooms
    /// instead of answering every offer with a refusal.</summary>
    internal string AdmissionState =>
        _admission.WouldRefuse(DegradedRoomCount())
            ? Chorus.Rpc.ProcessAdmission.Full
            : Chorus.Rpc.ProcessAdmission.Accepting;

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}

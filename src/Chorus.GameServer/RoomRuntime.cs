using System.Collections.Concurrent;
using System.Security.Cryptography;
using Chorus.Redesign.ExampleGame;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.GameServer;

/// <summary>
/// Owns the room actors of one game-server process (architecture 7.2). PrepareRoom derives the
/// deterministic initial state from the module configuration, deposits the tick-0 snapshot on
/// the data plane, activates the round and starts the actor. The process holds no database
/// credentials — every persistent fact travels through the data-plane channel.
/// </summary>
public sealed class RoomRuntime : IAsyncDisposable
{
    private readonly Grpc.Net.Client.GrpcChannel _dataChannel;
    private readonly RoundStore.RoundStoreClient _rounds;
    private readonly FrameStore.FrameStoreClient _frames;
    private readonly ConcurrentDictionary<string, RoomActor> _actors = new(StringComparer.Ordinal);

    public RoomRuntime(Grpc.Net.Client.GrpcChannel dataChannel)
    {
        _dataChannel = dataChannel;
        _rounds = new RoundStore.RoundStoreClient(dataChannel);
        _frames = new FrameStore.FrameStoreClient(dataChannel);
    }

    /// <summary>
    /// Prepares, activates and runs an admitted room. A second call for a room this process
    /// already knows answers duplicate without touching the room again — reruns of a finished
    /// or suspended room are a fresh room id until recovery lands (M2-4).
    /// </summary>
    public async Task<PrepareRoomReply> PrepareRoomAsync(PrepareRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.RoomId, "room_id"); Require(request.GameId, "game_id");
        Require(request.OwnerInstanceId, "owner_instance_id"); Require(request.OperationId, "operation_id");
        if (request.Epoch <= 0) throw new ArgumentException("The room epoch must be positive.");
        if (request.DurationTicks <= 0) throw new ArgumentException("The duration must be positive.");
        if (request.Participants.Count == 0)
            throw new ArgumentException("A round needs participants.");
        if (request.TickMs is < 1 or > 1000)
            throw new ArgumentException("The tick cadence is 1..1000 ms.");
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var participant in request.Participants)
        {
            Require(participant.BindingId, "participants.binding_id");
            Require(participant.ParticipantId, "participants.participant_id");
            Require(participant.Faction, "participants.faction");
            if (bindings.ContainsKey(participant.BindingId))
                throw new ArgumentException("Participant bindings must be distinct.");
            bindings[participant.BindingId] = participant.ParticipantId;
        }
        if (_actors.ContainsKey(request.RoomId))
            return new PrepareRoomReply
            {
                RoomId = request.RoomId, Duplicate = true, Status = "Prepared", StateVersion = 1
            };

        var manifest = new RedesignGameModuleManifest("example-factions", "1.0.0",
            request.TickMs > 0 ? request.TickMs : RedesignGameModuleManifest.Default.TickMs,
            RedesignGameModuleManifest.Default.SnapshotEveryTicks,
            RedesignGameModuleManifest.DeterministicReplay);
        var module = new ExampleGameModule(manifest);
        var initial = module.CreateInitialState(new GameStartConfig(request.DurationTicks,
            request.RandomSeed,
            request.Participants.Select(participant =>
                new GameParticipant(participant.ParticipantId, participant.Faction)).ToList(),
            request.FailAfterTick));
        var payload = module.Serialize(initial);
        var hash = SHA256.HashData(payload);
        var configHash = Convert.ToHexString(hash).ToLowerInvariant();

        // Data-plane receipts under deterministic operation ids: a retry after a lost reply
        // replays the stored receipts instead of re-deriving anything.
        await _rounds.PrepareRoundAsync(new PrepareRoundRequest
        {
            RoomId = request.RoomId, OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
            GameId = request.GameId, SnapshotPayload = ByteString.CopyFrom(payload),
            SnapshotHash = ByteString.CopyFrom(hash), SnapshotSchemaVersion = initial.SchemaVersion,
            OperationId = request.OperationId + "/prepare"
        }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        await _rounds.ActivateRoundAsync(new ActivateRoundRequest
        {
            RoomId = request.RoomId, OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
            GameId = request.GameId, OperationId = request.OperationId + "/activate"
        }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);

        var actor = new RoomActor(this, _frames, module, request.GameId, request.RoomId,
            request.OwnerInstanceId, request.Epoch, configHash, initial, bindings);
        if (!_actors.TryAdd(request.RoomId, actor))
        {
            await actor.DisposeAsync();
            return new PrepareRoomReply
            {
                RoomId = request.RoomId, Duplicate = true, Status = "Prepared", StateVersion = 1
            };
        }
        actor.Start();
        return new PrepareRoomReply
        {
            RoomId = request.RoomId, Duplicate = false, Status = "Running", StateVersion = 1
        };
    }

    /// <summary>
    /// Parks a damaged room visibly. The operation id is derived from the room, so a repeated
    /// suspension replays the stored receipt; a room already released or finished simply
    /// rejects — nothing here may take the host down with it.
    /// </summary>
    public async Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
        string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            await _rounds.SuspendRoundAsync(new SuspendRoundRequest
            {
                RoomId = roomId, GameId = gameId, OwnerInstanceId = ownerInstanceId, Epoch = epoch,
                Reason = reason, OperationId = $"{ownerInstanceId}/suspend/{roomId}"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // The suspension itself is best-effort from inside a failing actor; the room's
            // state stays inspectable on the data plane either way.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var actor in _actors.Values) await actor.DisposeAsync();
        _actors.Clear();
        _dataChannel.Dispose();
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}

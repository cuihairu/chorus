using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.GameServer;

/// <summary>
/// Owns the room actors of one game-server process (architecture 7.2). PrepareRoom derives the
/// deterministic initial state from the module configuration, deposits the tick-0 snapshot on
/// the data plane, activates the round and starts the actor. The process holds no database
/// credentials — every persistent fact travels through the data-plane channel.
/// </summary>
public sealed partial class RoomRuntime : IRoomLifecycle, IAsyncDisposable
{
    private readonly Grpc.Net.Client.GrpcChannel? _dataChannel;
    private readonly RoundStore.RoundStoreClient _rounds;
    private readonly FrameStore.FrameStoreClient _frames;
    private readonly Settlement.SettlementClient _settlement;
    private readonly RoomMetrics _metrics;
    // FIX-PERF-07: the single-host admission budget — active-room and measured-load caps a
    // new allocation is refused on. Recovery reserves unconditionally, so the counter tracks
    // every room the host actually holds, not only the ones admitted by Prepare.
    private readonly RoomAdmissionBudget _admission;
    private readonly RecoveryConcurrencyGate? _recoveryGate;
    private readonly int _overBudgetFactorOverride;
    private readonly int _overBudgetWindowOverride;
    private readonly ConcurrentDictionary<string, RoomActor> _actors = new(StringComparer.Ordinal);

    /// <summary>The host's event-chain counters (FIX-OBS-11): one process-wide instance
    /// every room actor bumps, the snapshot the self-report carries.</summary>
    public RoomEventCounters EventCounters { get; }

    // FIX-PERF-03: rooms whose actor has crossed the start line. Presence means Start ran;
    // a room held by _actors but absent here is prepared and waiting for ActivateRoom.
    private readonly ConcurrentDictionary<string, byte> _activated = new(StringComparer.Ordinal);
    // Per-room activation gates (FIX-ROOM-03): prepare and recovery reach the actor
    // dictionary only through their room's gate, so interleaved callers never both
    // decide the room is unknown and start a second loop.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _activations = new(StringComparer.Ordinal);
    // The bounded cache of released results (FIX-ROOM-05): a released room keeps its frozen
    // result queryable without keeping the actor; the oldest entries fall off the end.
    private readonly object _resultGate = new();
    private readonly Dictionary<string, FinalRoundResult> _finalResults = new(StringComparer.Ordinal);
    private readonly Queue<string> _releaseOrder = new();

    /// <summary>How many released rooms keep their result in memory; the rest fall back to
    /// the persistent result on the data plane.</summary>
    internal int RetainedResults { get; set; } = 256;

    // FIX-SYNC-09: the client command channel — same literal as RoomActor and the input
    // store's ClientPlatform, kept local because GameServer does not reference DataServer.
    private const string ClientCommandPlatform = RoomCommandPlatforms.Client;

    // FIX-INPUT-01: the direct-push room-event dispositions (strings; the protos carry no
    // enums by convention) and the host's inbound bound — a saturated or closed bound
    // refuses before any durable write, so Overloaded never depends on store state.
    public const string DurableAccepted = "DurableAccepted";
    public const string Duplicate = "Duplicate";
    public const string StaleOwner = "StaleOwner";
    public const string Overloaded = "Overloaded";
    // FIX-GM-07: the delegated dispatch's seat refusal — the named participant holds no
    // live binding for this room (unknown, finished, or pointed elsewhere). A retry cannot
    // fix it; the operator re-reads the room snapshot.
    public const string UnknownParticipant = "UnknownParticipant";
    public const string OutcomeUnknown = "OutcomeUnknown";
    private readonly SemaphoreSlim? _roomEventGate;
    // FIX-INPUT-03: the per-room inbound seat bound every constructed actor carries.
    private readonly int _inboundBound;
    private readonly IRoomInputTranslator? _inputTranslator;
    // FIX-END-04: the step-1 report of a frozen result; absent (no control endpoint
    // provisioned) the room's completion has no control-plane notification of its own —
    // the control plane's discovery sweep is the only reporter then.
    private readonly RoomEndReporter? _endReporter;

    /// <summary>This host's shipped packages by hash, and the cluster default hash it must
    /// carry (FIX-MODULE-04): the registry keeps every version, a host's runtime set can
    /// drop the old package of an upgrade — an empty package hash then names the cluster
    /// default only if this host still ships it.</summary>
    private readonly IReadOnlyDictionary<string, RedesignModulePackage> _packages;
    private readonly string _shippedList;
    private readonly string _defaultPackageHash;

    public RoomRuntime(Grpc.Net.Client.GrpcChannel dataChannel,
        int? maxActiveRooms = null, int? maxDegradedRooms = null,
        int? maxConcurrentRecoveries = null, int? maxQueuedRecoveries = null,
        IReadOnlyCollection<RedesignModulePackage>? shippedPackages = null,
        int roomEventInboundLimit = 256, int roomInboundPerRoom = 1024,
        RoomEndReporter? endReporter = null, IRoomInputTranslator? inputTranslator = null,
        RoomEventCounters? eventCounters = null)
        : this(new RoundStore.RoundStoreClient(dataChannel), new FrameStore.FrameStoreClient(dataChannel),
            new Settlement.SettlementClient(dataChannel), dataChannel,
            maxActiveRooms: maxActiveRooms, maxDegradedRooms: maxDegradedRooms,
            maxConcurrentRecoveries: maxConcurrentRecoveries,
            maxQueuedRecoveries: maxQueuedRecoveries, shippedPackages: shippedPackages,
            roomEventInboundLimit: roomEventInboundLimit, roomInboundPerRoom: roomInboundPerRoom,
            endReporter: endReporter, inputTranslator: inputTranslator,
            eventCounters: eventCounters) { }

    /// <summary>Test seam: the data-plane clients scripted, the channel optional.</summary>
    internal RoomRuntime(RoundStore.RoundStoreClient rounds, FrameStore.FrameStoreClient frames,
        Settlement.SettlementClient settlement, Grpc.Net.Client.GrpcChannel? dataChannel = null,
        RoomMetrics? metrics = null, int? maxActiveRooms = null, int? maxDegradedRooms = null,
        int overBudgetFactor = 0, int overBudgetWindow = 0,
        int? maxConcurrentRecoveries = null, int? maxQueuedRecoveries = null,
        IReadOnlyCollection<RedesignModulePackage>? shippedPackages = null,
        int roomEventInboundLimit = 256, int roomInboundPerRoom = 1024,
        RoomEndReporter? endReporter = null, IRoomInputTranslator? inputTranslator = null,
        RoomEventCounters? eventCounters = null)
    {
        _rounds = rounds;
        _frames = frames;
        _settlement = settlement;
        _dataChannel = dataChannel;
        _metrics = metrics ?? RoomMetrics.Default;
        EventCounters = eventCounters ?? new RoomEventCounters();
        // FIX-INPUT-08: the platform-event → module-command mapping every room and every
        // recovery replay runs through; null means the delivered payload is a command already.
        _inputTranslator = inputTranslator;
        _admission = new RoomAdmissionBudget(maxActiveRooms, maxDegradedRooms);
        _overBudgetFactorOverride = overBudgetFactor;
        _overBudgetWindowOverride = overBudgetWindow;
        // FIX-PERF-08: the concurrency gate exists only when configured — absent means an
        // explicit, not accidental, unbounded host. A mistyped bound fails closed above.
        _recoveryGate = maxConcurrentRecoveries is { } concurrent
            ? new RecoveryConcurrencyGate(concurrent, maxQueuedRecoveries ?? 0)
            : null;
        _packages = RedesignModuleCatalog.Index(shippedPackages ?? RedesignModuleCatalog.Shipped);
        _shippedList = RedesignModuleCatalog.ListOf(shippedPackages ?? RedesignModuleCatalog.Shipped);
        _defaultPackageHash = RedesignModuleCatalog.Default.PackageHash;
        // FIX-INPUT-01: a positive limit bounds concurrent inbound pushes; zero closes the
        // edge (every DeliverEvent answers Overloaded) — a choice, not an accident.
        _roomEventGate = roomEventInboundLimit > 0 ? new SemaphoreSlim(roomEventInboundLimit) : null;
        // FIX-INPUT-03: the per-room seat list every actor hands accepted events into; zero
        // closes every room's seats the same fail-closed way.
        _inboundBound = roomInboundPerRoom;
        _endReporter = endReporter;
    }

    /// <summary>Resolves a package against this host's shipped set. Empty names the
    /// cluster default (the coordinator's pin); an empty hash on a host that no longer
    /// ships the default answers false, so a prepare cannot fall through to a version the
    /// host does not actually load.</summary>
    private bool TryResolvePackage(string packageHash, out RedesignModulePackage package)
    {
        if (packageHash.Length > 0)
            return _packages.TryGetValue(packageHash, out package!);
        return _packages.TryGetValue(_defaultPackageHash, out package!);
    }

    /// <summary>How a refusal names the package it could not resolve: an empty hash is
    /// the cluster default, so the refusal names that hash rather than an empty string.</summary>
    private string PackageNameFor(string packageHash) =>
        packageHash.Length > 0 ? packageHash : $"{_defaultPackageHash} (the cluster default)";

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
        // FIX-ROOM-03: the check-activate-add sequence runs under the room's activation
        // gate, so a concurrent prepare or recovery for the same room waits and then sees
        // this call's outcome instead of racing past an empty dictionary.
        var activation = _activations.GetOrAdd(request.RoomId, static _ => new SemaphoreSlim(1, 1));
        await activation.WaitAsync(cancellationToken);
        try
        {
            if (_actors.ContainsKey(request.RoomId))
                return new PrepareRoomReply
                {
                    RoomId = request.RoomId, Duplicate = true, Status = "Prepared", StateVersion = 1
                };

            // FIX-MODULE-02: the module loads through the package registry — the package
            // the request names (empty = the cluster default, the same pin the
            // coordinator wrote at StartRound). A package this host does not ship is
            // refused by name before any capacity or data-plane work; the room is never
            // silently rebuilt under the default example module.
            if (!TryResolvePackage(request.PackageHash, out var package))
                throw new ModulePackageException(
                    $"The room names package \"{PackageNameFor(request.PackageHash)}\", which this " +
                    $"host does not ship (shipped: {_shippedList}); the package is missing " +
                    "or unknown.");

            // FIX-PERF-07: a new allocation reserves an admission slot before any data-plane
            // work, so a saturated host refuses — an existing room is never touched by a
            // refused admission — and concurrent prepares of different rooms cannot race past
            // the cap on a bare dictionary count. The slot is held for the room's lifetime and
            // returned on a failed creation, so a room that did not start never leaks capacity.
            var admission = _admission.TryReserve(DegradedRoomCount());
            if (admission == AdmissionOutcome.ActiveRoomCap)
                throw new RoomAdmissionException(
                    $"The game host already runs {_admission.ActiveRooms} active rooms, at " +
                    "its admission cap; the new allocation is refused.");
            if (admission == AdmissionOutcome.LoadBudget)
                throw new RoomAdmissionException(
                    "The game host's measured load is at its budget; a new allocation is " +
                    "refused until the overloaded rooms drain.");
            try
            {
                // FIX-MODULE-01: the room is prepared under the package's declared module
                // identity — the same authority the coordinator pinned at admission and a
                // recovery later validates the room against (FIX-MODULE-02: loaded
                // through the registry, never hand-built).
                var manifest = package.ManifestAt(
                    request.TickMs > 0 ? request.TickMs : package.TickMs);
                // FIX-SYNC-04: the offline policy is a declared module fact, never a host
                // default — a package that declares none, or one whose declaration the
                // round can never satisfy, is refused here before any data-plane work.
                // "Missing behavior" is a refusal, not a silent baseline (authoritative-sync.md:44).
                if (manifest.Offline is null)
                    throw new ModulePackageException(
                        $"The module package \"{package.PackageHash}\" declares no offline " +
                        "policy; a room cannot be admitted without the module's declared " +
                        "all-offline behavior (FIX-SYNC-04).");
                var offlineError = manifest.Offline.Validate(request.Participants.Count);
                if (offlineError is not null)
                    throw new ModulePackageException(
                        $"The module package \"{package.PackageHash}\" declares an illegal " +
                        $"offline policy: {offlineError}");
                var module = package.Load(manifest);
                var initial = module.CreateInitialState(new GameStartConfig(request.DurationTicks,
                    request.RandomSeed,
                    request.Participants.Select(participant =>
                        new GameParticipant(participant.ParticipantId, participant.Faction)).ToList(),
                    request.FailAfterTick));
                var payload = module.Serialize(initial);
                var hash = SHA256.HashData(payload);
                var configHash = Convert.ToHexString(hash).ToLowerInvariant();

                // Data-plane receipts under deterministic operation ids: a retry after a lost
                // reply replays the stored receipts instead of re-deriving anything.
                // FIX-MODULE-03: the room persists the package's recovery mode here — the
                // mode decides the checkpoint cadence, and a later recovery validates the
                // room against exactly this fact.
                await _rounds.PrepareRoundAsync(new PrepareRoundRequest
                {
                    RoomId = request.RoomId, OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
                    GameId = request.GameId, SnapshotPayload = ByteString.CopyFrom(payload),
                    SnapshotHash = ByteString.CopyFrom(hash), SnapshotSchemaVersion = initial.SchemaVersion,
                    OperationId = request.OperationId + "/prepare", TickMs = request.TickMs,
                    RecoveryMode = package.RecoveryMode,
                    OfflinePolicy = manifest.Offline.Canonical
                }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
                // A deferred prepare holds the room — round Preparing, actor not ticking — for
                // ActivateRoom to release across a common start line (FIX-PERF-03).
                if (!request.DeferredActivation)
                    await _rounds.ActivateRoundAsync(new ActivateRoundRequest
                    {
                        RoomId = request.RoomId, OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
                        GameId = request.GameId, OperationId = request.OperationId + "/activate"
                    }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);

                var actor = new RoomActor(this, _frames, module, request.GameId, request.RoomId,
                    request.OwnerInstanceId, request.Epoch, configHash, initial, bindings,
                    hangAfterTick: request.HangAfterTick, hangMs: request.HangMs, metrics: _metrics,
                    overBudgetFactor: _overBudgetFactorOverride,
                    overBudgetWindow: _overBudgetWindowOverride, inboundBound: _inboundBound,
                    inputTranslator: _inputTranslator, counters: EventCounters);
                actor.LoopEnded = Retire;
                if (!_actors.TryAdd(request.RoomId, actor))
                {
                    _admission.Release(); // the room was never held; the slot is free again
                    await actor.DisposeAsync();
                    return new PrepareRoomReply
                    {
                        RoomId = request.RoomId, Duplicate = true, Status = "Prepared", StateVersion = 1
                    };
                }
                if (request.DeferredActivation)
                {
                    actor.HoldForCommonStart();
                    return new PrepareRoomReply
                    {
                        RoomId = request.RoomId, Duplicate = false, Status = "Prepared", StateVersion = 1
                    };
                }
                actor.Start();
                _activated.TryAdd(request.RoomId, 0);
                return new PrepareRoomReply
                {
                    RoomId = request.RoomId, Duplicate = false, Status = "Running", StateVersion = 1
                };
            }
            catch
            {
                _admission.Release(); // failed creation releases capacity
                throw;
            }
        }
        finally
        {
            activation.Release();
        }
    }

    /// <summary>
    /// Releases a room prepared with deferred activation (FIX-PERF-03): the round flips to
    /// Running and the actor starts. A retry after a lost reply answers duplicate — Start
    /// never runs twice — and a fence that does not match the held room never releases it.
    /// FIX-REC-01: a room held by a deferred recovery releases through the fenced ownership
    /// confirm instead of the round flip — the room was never Preparing, and the grant is
    /// what proves the route barrier already admitted it.
    /// </summary>
    public async Task<ActivateRoomReply> ActivateRoomAsync(ActivateRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.RoomId, "room_id"); Require(request.GameId, "game_id");
        Require(request.OwnerInstanceId, "owner_instance_id"); Require(request.OperationId, "operation_id");
        if (request.Epoch <= 0) throw new ArgumentException("The room epoch must be positive.");
        var activation = _activations.GetOrAdd(request.RoomId, static _ => new SemaphoreSlim(1, 1));
        await activation.WaitAsync(cancellationToken);
        try
        {
            if (!_actors.TryGetValue(request.RoomId, out var actor))
                throw new ArgumentException(
                    $"The room {request.RoomId} is not held by this host.");
            if (actor.GameId != request.GameId || actor.OwnerInstanceId != request.OwnerInstanceId ||
                actor.Epoch != request.Epoch)
                throw new ArgumentException(
                    "The activation fence does not match the held room.");
            if (!_activated.TryAdd(request.RoomId, 0))
                return new ActivateRoomReply
                {
                    RoomId = request.RoomId, Duplicate = true, Status = "Running"
                };
            try
            {
                if (actor.HeldFromRecovery)
                {
                    // FIX-REC-01: the deferred recovery's activation grant is the fenced
                    // ownership confirm (FIX-TAKE-03) — the room was never Preparing, so
                    // there is no round to flip; the confirm flips Recovering→Active for
                    // exactly this owner and the loop may only start once it held.
                    await _rounds.ConfirmRoomOwnershipAsync(new ConfirmRoomOwnershipRequest
                    {
                        GameId = request.GameId, RoomId = request.RoomId,
                        OwnerInstance = request.OwnerInstanceId
                    }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
                }
                else
                {
                    // Running before the first tick: the actor's first commit must meet a Running
                    // round, not race the flip.
                    await _rounds.ActivateRoundAsync(new ActivateRoundRequest
                    {
                        RoomId = request.RoomId, OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
                        GameId = request.GameId, OperationId = request.OperationId
                    }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
                }
            }
            catch
            {
                // The start-line flag only means "released" once the round flip held: a failed
                // activation rolls it back, or a retry would answer duplicate and leave the
                // room held forever (the stall governor rightly ignores Preparing).
                _activated.TryRemove(new KeyValuePair<string, byte>(request.RoomId, 0));
                throw;
            }
            actor.ReleaseFromHold();
            actor.Start();
            return new ActivateRoomReply
            {
                RoomId = request.RoomId, Duplicate = false, Status = "Running"
            };
        }
        finally
        {
            activation.Release();
        }
    }

    /// <summary>The actor of a room this host runs, or null — subscribers attach to it.</summary>
    internal RoomActor? FindRoom(string roomId) =>
        _actors.TryGetValue(roomId, out var actor) ? actor : null;

    private readonly ConcurrentDictionary<string, RoomActorStatus> _retiredStatuses =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Drops an actor whose loop ended without a committed result (FIX-ROOM-04): a stopped
    /// or disposed actor leaves the dictionary so the room id stops masquerading as running.
    /// The removal is identity-checked, so a delayed retirement never removes a newer actor
    /// that has since taken the room; a completed room keeps its actor for the result tail
    /// until release (FIX-END-14). Repeated retirement is a no-op. The final status stays
    /// behind so the progress report keeps identifying the stopped room (FIX-TAKE-06).
    /// </summary>
    internal void Retire(RoomActor actor)
    {
        var status = actor.Status();
        if (status.State is RoomRunState.Running or RoomRunState.Completed) return;
        _retiredStatuses[actor.RoomId] = status;
        // FIX-PERF-07: the room left the host, so its admission slot returns — gated on the
        // removal so a raced teardown (release/dispose already retired it) never double-frees.
        if (((ICollection<KeyValuePair<string, RoomActor>>)_actors)
            .Remove(new KeyValuePair<string, RoomActor>(actor.RoomId, actor)))
            _admission.Release();
        // The start-line flag follows the actor out, so a room id re-prepared later starts
        // from a clean slate rather than answering activations as duplicates (FIX-PERF-03).
        _activated.TryRemove(new KeyValuePair<string, byte>(actor.RoomId, 0));
    }

    /// <summary>
    /// Every room's progress this host can report (FIX-TAKE-06): the live actors plus the
    /// final status a retired or released actor left behind — a stopped room stays
    /// identified for as long as this process runs, even after its object is gone.
    /// </summary>
    internal IReadOnlyList<(string RoomId, RoomActorStatus Status)> RoomStatuses()
    {
        var statuses = new List<(string RoomId, RoomActorStatus Status)>(_actors.Count);
        foreach (var (roomId, actor) in _actors)
            statuses.Add((roomId, actor.Status()));
        foreach (var (roomId, status) in _retiredStatuses)
            if (!_actors.ContainsKey(roomId))
                statuses.Add((roomId, status));
        return statuses;
    }

    /// <summary>The frozen result of a released room, or null (FIX-ROOM-05).</summary>
    internal FinalRoundResult? FindResult(string roomId)
    {
        lock (_resultGate) return _finalResults.TryGetValue(roomId, out var result) ? result : null;
    }

    /// <summary>
    /// Releases a finished room (the FIX-END-14 hook, FIX-ROOM-05): the actor leaves the
    /// dictionary and its frozen result moves into the bounded cache, so the outcome stays
    /// queryable after the room itself is gone. A room that has not committed its result is
    /// never released; releasing an unknown or already-released room simply answers false.
    /// </summary>
    internal async Task<bool> ReleaseRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        var activation = _activations.GetOrAdd(roomId, static _ => new SemaphoreSlim(1, 1));
        await activation.WaitAsync(cancellationToken);
        try
        {
            if (!_actors.TryGetValue(roomId, out var actor)) return false;
            var final = actor.FinalResult();
            if (final is null) return false; // a live room is not release material
            await actor.DisposeAsync();
            _retiredStatuses[roomId] = actor.Status();
            // FIX-PERF-07: the slot returns with the room. A completed room's actor stays in
            // the dictionary through its teardown (Retire leaves Completed rooms alone), so
            // this removal is the one that frees it; the gate makes a raced teardown safe.
            if (_actors.TryRemove(new KeyValuePair<string, RoomActor>(roomId, actor)))
                _admission.Release();
            lock (_resultGate)
            {
                if (!_finalResults.ContainsKey(roomId)) _releaseOrder.Enqueue(roomId);
                _finalResults[roomId] = final;
                while (_finalResults.Count > RetainedResults)
                    _finalResults.Remove(_releaseOrder.Dequeue());
            }
            return true;
        }
        finally
        {
            activation.Release();
        }
    }

    /// <summary>
    /// Destroys a released round's runtime entity on this host (FIX-END-14): the actor
    /// leaves the dictionary whatever its state — a completed room's frozen result moves
    /// into the bounded cache on the way out, a live leftover of the settled round is
    /// disposed (its authority ended at the release; the dispose is local teardown, the
    /// data plane is the truth). The epoch ceiling is the released ownership row's own
    /// bumped epoch: an actor at or above it belongs to a newer generation and is never
    /// touched. Idempotent and never throwing: an unknown or already-destroyed room
    /// answers (false, false), a spared newer generation answers (false, true).
    /// </summary>
    internal async Task<(bool Destroyed, bool SparedNewGeneration)> DestroyRoomAsync(
        string roomId, long releaseEpoch, CancellationToken cancellationToken = default)
    {
        var activation = _activations.GetOrAdd(roomId, static _ => new SemaphoreSlim(1, 1));
        await activation.WaitAsync(cancellationToken);
        try
        {
            if (!_actors.TryGetValue(roomId, out var actor)) return (false, false);
            if (actor.Epoch >= releaseEpoch) return (false, true);
            var final = actor.FinalResult();
            await actor.DisposeAsync();
            _retiredStatuses[roomId] = actor.Status();
            if (_actors.TryRemove(new KeyValuePair<string, RoomActor>(roomId, actor)))
                _admission.Release();
            if (final is not null)
                lock (_resultGate)
                {
                    if (!_finalResults.ContainsKey(roomId)) _releaseOrder.Enqueue(roomId);
                    _finalResults[roomId] = final;
                    while (_finalResults.Count > RetainedResults)
                        _finalResults.Remove(_releaseOrder.Dequeue());
                }
            // The start-line flag follows the room out, exactly as a retirement drops it.
            _activated.TryRemove(new KeyValuePair<string, byte>(roomId, 0));
            return (true, false);
        }
        finally
        {
            activation.Release();
        }
    }

    /// <summary>
    /// The late-subscriber contract of a released room (FIX-ROOM-05): a subscriber on the
    /// result's epoch receives the frozen result and the stream ends; a stale epoch is told
    /// the epoch it must resubscribe at. A room with no cached result never reaches here —
    /// the caller reports the room unknown instead.
    /// </summary>
    internal static SyncMessage LateSubscriberAnswer(string roomId, long requestedEpoch,
        FinalRoundResult final) =>
        requestedEpoch == final.Epoch
            ? new SyncMessage { RoundResult = new RoundResultMessage
                {
                    RoomId = roomId, Epoch = final.Epoch, Tick = final.Tick,
                    StateVersion = final.StateVersion, ResultJson = final.ResultJson
                } }
            : new SyncMessage { ResyncRequired = new ResyncRequiredMessage
                {
                    RoomId = roomId, CurrentEpoch = final.Epoch,
                    Reason = $"The room moved to epoch {final.Epoch}."
                } };
}

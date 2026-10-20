using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Chorus.Engine.Abstractions;
using Chorus.Protocol.Messages;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Session;

public sealed class SessionActor : IAsyncDisposable
{
    private interface ICommand;

    private sealed record AddClientCmd(ISessionClient Client) : ICommand;
    private sealed record RemoveClientCmd(string ClientId) : ICommand;
    private sealed record AckCmd(string ClientId, long LastAppliedTickId, long ClientTimeMs) : ICommand;
    private sealed record ResyncCmd(string ClientId, long FromFrameId) : ICommand;
    private sealed record IngestAudienceEventCmd(AudienceEvent Event) : ICommand;

    // Two lanes (review stage-2): control commands are unbounded and never
    // dropped; audience events go through a bounded lane so a flood back-pressures
    // the ingress instead of growing memory without limit.
    private readonly Channel<ICommand> _control;
    private readonly Channel<IngestAudienceEventCmd> _events;

    private readonly Dictionary<string, (OutboundClientQueue Client, SessionClientState State)> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<AudienceEvent>> _scheduledByTick = new();

    // Frame-sync P2: canonical snapshots retained for client gap-fill, and the
    // per-tick event accumulator that feeds the snapshot's frameEvents envelope.
    private readonly FrameHistory _frameHistory;
    private readonly List<(long TickId, IReadOnlyList<AudienceEvent> Events)> _pendingFrameEvents = new();
    private const int MaxPendingFrameEvents = 4096;

    // Reused across ticks: the snapshot size cap only needs the state's
    // serialized length, so the tick body must not allocate the state's bytes.
    private Utf8JsonWriter? _stateProbe;

    private readonly SessionConfig _config;
    private readonly IGameModule _game;
    private readonly IAudienceEventReducer _reducer;
    private readonly ISessionTickDurability? _durability;
    private readonly HashSet<string> _durableScheduled = new(StringComparer.Ordinal);
    public bool DurabilityFailed { get; private set; }
    private volatile SessionDiagnostics _diagnostics;

    private long _totalIngestedEvents;
    private long _totalAppliedEvents;
    private long _totalMergedEvents;
    private long _totalDroppedEvents;
    private long _totalSnapshotsSent;
    private long _totalResyncSnapshotsSent;
    private long _totalAcksAccepted;
    private long _totalAcksDuplicate;
    private long _totalAcksStale;
    private long _totalAcksInvalid;
    private long _totalDroppedOverflow;
    private long _totalDroppedGifts;
    private long _totalTickOverruns;
    private long _totalTickErrors;
    private long _totalSnapshotsOversize;
    private long _lastSnapshotBytes;
    private long _totalResyncRequests;
    private long _totalResyncReplays;
    private long _totalResyncWindowMisses;
    private long _totalResyncThrottled;
    private long _totalResyncReplayCapped;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private int _disposed;

    public string SessionId { get; }
    public long TickId { get; private set; }

    public SessionActor(
        string sessionId,
        SessionConfig config,
        IGameModule game,
        IAudienceEventReducer? reducer = null,
        ISessionTickDurability? durability = null,
        long restoredTick = 0)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("SessionId is required.", nameof(sessionId));
        }

        SessionId = sessionId;
        _config = config;
        _durability = durability;
        TickId = restoredTick;
        _game = game;
        _reducer = reducer ?? new DefaultAudienceEventReducer();

        _control = Channel.CreateUnbounded<ICommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _events = Channel.CreateBounded<IngestAudienceEventCmd>(new BoundedChannelOptions(Math.Max(1, config.MaxPendingEvents))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _frameHistory = new FrameHistory(config.FrameHistoryBytes);

        _diagnostics = new SessionDiagnostics(
            SessionId: SessionId,
            TickId: 0,
            TickDurationMs: _config.TickDurationMs,
            ServerTimeMs: NowMs(),
            TotalIngestedEvents: 0,
            TotalAppliedEvents: 0,
            TotalMergedEvents: 0,
            TotalDroppedEvents: 0,
            TotalSnapshotsSent: 0,
            TotalResyncSnapshotsSent: 0,
            TotalAcksAccepted: 0,
            TotalAcksDuplicate: 0,
            TotalAcksStale: 0,
            TotalAcksInvalid: 0,
            LastTickProcessMs: 0,
            Clients: Array.Empty<SessionClientDiagnostics>()
        );
    }

    public SessionDiagnostics Diagnostics => _diagnostics;

    public void Start()
    {
        if (_runTask != null)
        {
            return;
        }

        _runCts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.TickDurationMs));
        _runTask = RunLoopAsync(_runCts.Token);
    }

    public async ValueTask StopAsync()
    {
        if (_runTask == null || _runCts == null)
        {
            return;
        }

        _runCts.Cancel();

        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Dispose the timer only after the loop has stopped waiting on it;
            // disposing a PeriodicTimer with a pending waiter faults the loop.
            _timer?.Dispose();
            _runTask = null;
            _runCts.Dispose();
            _runCts = null;

            foreach (var (_, client) in _clients)
            {
                await client.Client.DisposeAsync().ConfigureAwait(false);
            }

            _clients.Clear();
        }
    }

    public ValueTask AddClientAsync(ISessionClient client)
        => _control.Writer.WriteAsync(new AddClientCmd(client));

    public ValueTask RemoveClientAsync(string clientId)
        => _control.Writer.WriteAsync(new RemoveClientCmd(clientId));

    public ValueTask AckAsync(string clientId, long lastAppliedTickId, long clientTimeMs)
        => _control.Writer.WriteAsync(new AckCmd(clientId, lastAppliedTickId, clientTimeMs));

    /// <summary>
    /// Client-initiated gap-fill request (frame-sync P2): the client reports the
    /// last frame it applied; the actor replays the retained window from its
    /// frame history, or answers with the latest state (forced) when the window
    /// start is no longer retained. Per-client cooldown throttles repeats.
    /// </summary>
    public ValueTask ResyncAsync(string clientId, long fromFrameId)
        => _control.Writer.WriteAsync(new ResyncCmd(clientId, fromFrameId));

    /// <summary>
    /// Backpressured ingest (review stage-2): returns false when the bounded
    /// event lane is full. Mergeable/cosmetic events drop immediately (counted
    /// as overflow); gifts - the must-not-lose class - get one bounded wait
    /// (<see cref="SessionConfig.EventEnqueueMaxWaitMs"/>) before being
    /// rejected as Failed so the caller (ingress) can signal a retry.
    /// </summary>
    public async ValueTask<bool> TryIngestAudienceEventAsync(AudienceEvent e)
    {
        var cmd = new IngestAudienceEventCmd(e);
        if (_events.Writer.TryWrite(cmd))
        {
            return true;
        }

        var isGift = e.Kind == AudienceEventKind.Gift;
        if (isGift && _config.EventEnqueueMaxWaitMs > 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(_config.EventEnqueueMaxWaitMs);
                if (await _events.Writer.WaitToWriteAsync(cts.Token).ConfigureAwait(false) && _events.Writer.TryWrite(cmd))
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (isGift)
        {
            Interlocked.Increment(ref _totalDroppedGifts);
        }
        else
        {
            Interlocked.Increment(ref _totalDroppedOverflow);
        }

        return false;
    }

    /// <summary>Compatibility wrapper: fire-and-forget ingest, result ignored.</summary>
    public async ValueTask IngestAudienceEventAsync(AudienceEvent e)
        => await TryIngestAudienceEventAsync(e).ConfigureAwait(false);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        if (_timer == null)
        {
            throw new InvalidOperationException("Start() must be called before running.");
        }

        // PeriodicTimer allows only one pending WaitForNextTickAsync at a time.
        // Keep the pending tick task across lane drains; re-calling the wait
        // while one is pending throws InvalidOperationException and would kill
        // the loop silently after the first command-driven batch.
        Task<bool>? tickTask = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (tickTask == null)
            {
                try
                {
                    tickTask = _timer.WaitForNextTickAsync(cancellationToken).AsTask();
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    break;
                }
            }

            var controlTask = _control.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var eventTask = _events.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(tickTask, controlTask, eventTask).ConfigureAwait(false);

            if (completed == controlTask)
            {
                while (_control.Reader.TryRead(out var cmd))
                {
                    await HandleCommandAsync(cmd, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (completed == eventTask)
            {
                while (_events.Reader.TryRead(out var cmd))
                {
                    ScheduleAudienceEvent(cmd.Event);
                }

                continue;
            }

            bool ticked;
            try
            {
                ticked = await tickTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            tickTask = null;

            if (!ticked)
            {
                break;
            }

            while (_control.Reader.TryRead(out var cmd))
            {
                await HandleCommandAsync(cmd, cancellationToken).ConfigureAwait(false);
            }

            while (_events.Reader.TryRead(out var eventCmd))
            {
                ScheduleAudienceEvent(eventCmd.Event);
            }

            try
            {
                await OnTickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Review stage-2: a throwing GameModule/reducer must not
                // silently kill the actor - the tick is lost, the loop lives.
                Interlocked.Increment(ref _totalTickErrors);
            }
        }
    }

    private async ValueTask HandleCommandAsync(ICommand cmd, CancellationToken cancellationToken)
    {
        switch (cmd)
        {
            case AddClientCmd c:
                {
                    var now = NowMs();
                    // Review stage-2: sends go through a latest-wins queue so a
                    // slow client never blocks the tick. Re-adding the same id
                    // replaces (and stops) the previous queue.
                    if (_clients.Remove(c.Client.ClientId, out var previous))
                    {
                        await previous.Client.DisposeAsync().ConfigureAwait(false);
                    }

                    _clients[c.Client.ClientId] = (new OutboundClientQueue(c.Client), new SessionClientState
                    {
                        ClientId = c.Client.ClientId,
                        ConnectedTickId = TickId,
                        LastAckTickId = TickId,
                        LastAckTimeMs = now,
                        LastResyncTimeMs = 0,
                        ResyncCount = 0,
                    });
                }
                break;
            case RemoveClientCmd c:
                if (_clients.Remove(c.ClientId, out var removed))
                {
                    await removed.Client.DisposeAsync().ConfigureAwait(false);
                }

                break;
            case AckCmd c:
                HandleAck(c);
                break;
            case ResyncCmd c:
                HandleResync(c);
                break;
            default:
                throw new InvalidOperationException($"Unknown command: {cmd.GetType().Name}");
        }
    }

    /// <summary>
    /// ACK validation (review P0-5): an ACK must be bound to a known client,
    /// never negative, never beyond the server's current TickId, and never a
    /// regression of recorded progress. Classes: invalid (negative / future /
    /// unknown client), duplicate (same tick re-acked), stale (regression),
    /// accepted (real progress) - all counted in diagnostics.
    /// </summary>
    private void HandleAck(AckCmd c)
    {
        if (c.LastAppliedTickId < 0)
        {
            Interlocked.Increment(ref _totalAcksInvalid);
            return;
        }

        if (!_clients.TryGetValue(c.ClientId, out var item) || c.LastAppliedTickId > TickId)
        {
            Interlocked.Increment(ref _totalAcksInvalid);
            return;
        }

        var last = item.State.LastAckTickId;
        if (c.LastAppliedTickId == last)
        {
            Interlocked.Increment(ref _totalAcksDuplicate);
            return;
        }

        if (c.LastAppliedTickId < last)
        {
            Interlocked.Increment(ref _totalAcksStale);
            return;
        }

        item.State.LastAckTickId = c.LastAppliedTickId;
        item.State.LastAckTimeMs = NowMs();
        Interlocked.Increment(ref _totalAcksAccepted);
    }

    /// <summary>
    /// Client-initiated gap-fill (frame-sync P2, docs/design/frame-sync.md §4.3):
    /// a client that detected a gap sends the last frame it applied. When the
    /// frame-history window still starts at or before that frame and the window
    /// fits within <c>MaxResyncReplayTicks</c>, the retained frames are
    /// replayed in order over a direct-send path (bypassing the latest-wins
    /// queue); otherwise the client gets the latest state with forced=true -
    /// because it missed more than the buffer holds, or because the window is
    /// larger than the replay cap (a full-state snapshot supersedes any number
    /// of stale frames). Requests repeated inside the per-client cooldown are
    /// throttled. The replay runs detached so a slow transport cannot stall the
    /// tick loop; ordering against later broadcasts is preserved by the
    /// outbound queue's send gate.
    /// </summary>
    private void HandleResync(ResyncCmd c)
    {
        Interlocked.Increment(ref _totalResyncRequests);

        if (!_clients.TryGetValue(c.ClientId, out var item))
        {
            return;
        }

        var now = NowMs();
        var state = item.State;
        if (_config.ResyncRequestCooldownMs > 0
            && state.LastResyncRequestTimeMs > 0
            && now - state.LastResyncRequestTimeMs < _config.ResyncRequestCooldownMs)
        {
            Interlocked.Increment(ref _totalResyncThrottled);
            return;
        }

        state.LastResyncRequestTimeMs = now;

        // Bounded replay (Session:MaxResyncReplayTicks, todo.md:156 churn blocker ③):
        // snapshots carry full state, so replaying a window larger than the cap
        // only multiplies eng→gw volume (a far-behind client gains nothing from
        // N-1 stale frames it will immediately be replaced by) - such requests
        // fall through to the one forced snapshot below.
        var windowHit = _frameHistory.TryGetWindow(c.FromFrameId, out var frames);
        if (windowHit && _config.MaxResyncReplayTicks > 0 && frames.Count <= _config.MaxResyncReplayTicks)
        {
            Interlocked.Increment(ref _totalResyncReplays);
            _ = item.Client.SendReplayAsync(frames, CancellationToken.None);
            return;
        }

        if (windowHit)
        {
            Interlocked.Increment(ref _totalResyncReplayCapped);
        }
        else
        {
            Interlocked.Increment(ref _totalResyncWindowMisses);
        }
        var latest = new SessionSnapshot(
            SessionId: SessionId,
            TickId: TickId,
            ServerTimeMs: now,
            State: _game.GetStateSnapshot(),
            Forced: true,
            Reason: "resync-gap",
            TargetClientId: c.ClientId,
            FrameEvents: DrainFrameEvents());
        var latestWire = WireSnapshot.Encode(latest);
        RecordSnapshotBytes(latestWire);
        _ = item.Client.SendSnapshotAsync(latestWire, CancellationToken.None);
        Interlocked.Increment(ref _totalResyncSnapshotsSent);
    }

    /// <summary>
    /// Builds the frameEvents envelope (events applied since the previous
    /// snapshot, each stamped with its applied tickId) and clears the
    /// accumulator. Empty history yields null so the wire omits the field.
    /// </summary>
    private List<FrameEvent>? DrainFrameEvents()
    {
        if (_pendingFrameEvents.Count == 0)
        {
            return null;
        }

        var events = new List<FrameEvent>();
        foreach (var (tickId, batch) in _pendingFrameEvents)
        {
            foreach (var e in batch)
            {
                events.Add(new FrameEvent(tickId, e.EventId, e.Kind.ToString().ToLowerInvariant(), e.UserId));
            }
        }

        _pendingFrameEvents.Clear();
        return events;
    }

    private void ScheduleAudienceEvent(AudienceEvent e)
    {
        Interlocked.Increment(ref _totalIngestedEvents);
        var targetTick = TickId + Math.Max(1,_config.InputDelayTicks);
        if (!_scheduledByTick.TryGetValue(targetTick, out var list))
        {
            list = new List<AudienceEvent>();
            _scheduledByTick[targetTick] = list;
        }
        list.Add(e);
    }

    private async Task OnTickAsync(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (_durability is not null)
        {
            if (DurabilityFailed) return;
            try
            {
                var pending = await _durability.PrepareAsync(cancellationToken).ConfigureAwait(false);
                foreach (var e in pending)
                    if (_durableScheduled.Add(JsonSerializer.Serialize(new[] { e.Platform, e.EventId }))) ScheduleAudienceEvent(e);
            }
            catch { DurabilityFailed = true; throw; }
        }
        TickId++;

        try
        {
            await TickBodyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_durability is not null) DurabilityFailed = true;
            throw;
        }
        finally
        {
            // Diagnostics must advance even for failed ticks, otherwise an
            // exception anywhere above freezes the externally visible TickId.
            sw.Stop();
            if (_config.TickTimeoutMs > 0 && sw.ElapsedMilliseconds > _config.TickTimeoutMs)
            {
                Interlocked.Increment(ref _totalTickOverruns);
            }

            UpdateDiagnostics(sw.ElapsedMilliseconds);
        }
    }

    private async Task TickBodyAsync(CancellationToken cancellationToken)
    {
        if (_scheduledByTick.TryGetValue(TickId, out var events))
        {
            _scheduledByTick.Remove(TickId);
        }
        else
        {
            events = null;
        }

        IReadOnlyList<AudienceEvent> tickEvents = Array.Empty<AudienceEvent>();
        if (events is { Count: > 0 })
        {
            events.Sort(static (a, b) =>
            {
                var t = a.IngestTimeMs.CompareTo(b.IngestTimeMs);
                return t != 0 ? t : string.CompareOrdinal(a.EventId, b.EventId);
            });

            var reduced = _reducer.Reduce(events, _durability is null ? _config.MaxEventsPerTick : 0);
            tickEvents = reduced.Events;
            Interlocked.Add(ref _totalAppliedEvents, reduced.Events.Count);
            if (reduced.MergedInputEvents > 0)
            {
                Interlocked.Add(ref _totalMergedEvents, reduced.MergedInputEvents);
            }
            if (reduced.DroppedInputEvents > 0)
            {
                Interlocked.Add(ref _totalDroppedEvents, reduced.DroppedInputEvents);
            }
        }

        _game.ApplyEvents(TickId, tickEvents);
        if (_durability is not null)
        {
            await _durability.CommitAsync(TickId - 1, TickId, _game.GetStateSnapshot(), events ?? [], cancellationToken).ConfigureAwait(false);
            foreach (var e in events ?? []) _durableScheduled.Remove(JsonSerializer.Serialize(new[] { e.Platform, e.EventId }));
        }

        // Frame-sync P2: accumulate per-tick events for the frameEvents envelope.
        // Bounded: when the accumulator exceeds MaxPendingFrameEvents the oldest
        // ticks drop (their state effect is still inside the snapshot).
        if (tickEvents.Count > 0)
        {
            _pendingFrameEvents.Add((TickId, tickEvents));
            var total = 0;
            for (var i = _pendingFrameEvents.Count - 1; i >= 0; i--)
            {
                total += _pendingFrameEvents[i].Events.Count;
                if (total > MaxPendingFrameEvents)
                {
                    _pendingFrameEvents.RemoveRange(0, i + 1);
                    break;
                }
            }
        }

        var now = NowMs();
        var needsPeriodic = _config.SnapshotEveryTicks > 0 && (TickId % _config.SnapshotEveryTicks) == 0;
        var forceClients = GetClientsNeedingResync(now);
        if (needsPeriodic || forceClients.Count > 0)
        {
            var state = _game.GetStateSnapshot();

            // Snapshot size cap (review stage-3): the serialized state payload
            // dominates wire size. An oversize state skips the send entirely -
            // clients fall back to their resync loop (no ACK -> forced resync
            // once the state shrinks back under the cap).
            if (_config.MaxSnapshotBytes > 0 && !StateWithinCap(state))
            {
                Interlocked.Increment(ref _totalSnapshotsOversize);
                return;
            }

            // Frame-sync P2: build the frameEvents envelope once per snapshot
            // tick and clear the accumulator (events not yet delivered in any
            // snapshot stay accumulated; the oversize early-return above keeps
            // them pending for the next snapshot).
            var frameEvents = DrainFrameEvents();

            if (needsPeriodic)
            {
                if (forceClients.Count == 0)
                {
                    // Broadcast fast path (todo §8 P3 snapshot serialization):
                    // every client gets the identical snapshot, so encode the
                    // wire bytes once per tick and let each transport reuse them.
                    // Forced stays explicitly false - the wire omits nulls, so a
                    // null here would change the payload shape clients parse.
                    var wire = WireSnapshot.Encode(NewBroadcastSnapshot(now, state, frameEvents));
                    RecordSnapshotBytes(wire);
                    _frameHistory.Add(wire);
                    foreach (var (client, _) in _clients.Values)
                    {
                        await client.SendSnapshotAsync(wire, cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref _totalSnapshotsSent);
                    }

                    return;
                }

                // Mixed path: non-forced clients receive exactly the broadcast
                // payload, so encode it once and share the wire across them and
                // the frame-history entry; only forced clients need their own
                // encode (targetClientId / lag metadata differ per client).
                WireSnapshot? broadcastWire = null;
                if (_config.FrameHistoryBytes > 0)
                {
                    broadcastWire = WireSnapshot.Encode(NewBroadcastSnapshot(now, state, frameEvents));
                    _frameHistory.Add(broadcastWire);
                }

                foreach (var (client, stateInfo) in _clients.Values)
                {
                    if (!forceClients.TryGetValue(stateInfo.ClientId, out var forceInfo))
                    {
                        broadcastWire ??= WireSnapshot.Encode(NewBroadcastSnapshot(now, state, frameEvents));
                        RecordSnapshotBytes(broadcastWire);
                        await client.SendSnapshotAsync(broadcastWire, cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref _totalSnapshotsSent);
                        continue;
                    }

                    var snapshot = new SessionSnapshot(
                        SessionId: SessionId,
                        TickId: TickId,
                        ServerTimeMs: now,
                        State: state,
                        Forced: true,
                        Reason: "lag",
                        TargetClientId: stateInfo.ClientId,
                        ClientLagTicks: forceInfo.LagTicks,
                        ClientLastAckTickId: forceInfo.LastAckTickId,
                        FrameEvents: frameEvents
                    );
                    var forcedWire = WireSnapshot.Encode(snapshot);
                    RecordSnapshotBytes(forcedWire);
                    await client.SendSnapshotAsync(forcedWire, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _totalSnapshotsSent);
                    stateInfo.LastResyncTimeMs = now;
                    stateInfo.ResyncCount++;
                    Interlocked.Increment(ref _totalResyncSnapshotsSent);
                }
            }
            else
            {
                foreach (var (client, stateInfo) in _clients.Values)
                {
                    if (!forceClients.TryGetValue(stateInfo.ClientId, out var forceInfo))
                    {
                        continue;
                    }
                    var snapshot = new SessionSnapshot(
                        SessionId: SessionId,
                        TickId: TickId,
                        ServerTimeMs: now,
                        State: state,
                        Forced: true,
                        Reason: "lag",
                        TargetClientId: stateInfo.ClientId,
                        ClientLagTicks: forceInfo.LagTicks,
                        ClientLastAckTickId: forceInfo.LastAckTickId,
                        FrameEvents: frameEvents
                    );
                    var forcedWire = WireSnapshot.Encode(snapshot);
                    RecordSnapshotBytes(forcedWire);
                    await client.SendSnapshotAsync(forcedWire, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _totalSnapshotsSent);
                    stateInfo.LastResyncTimeMs = now;
                    stateInfo.ResyncCount++;
                    Interlocked.Increment(ref _totalResyncSnapshotsSent);
                }
            }
        }

    }

    /// <summary>
    /// Broadcast-shaped snapshot shared by the frame-history entry and every
    /// non-forced client on a mixed tick. The wire omits nulls, so this is
    /// byte-identical to what the per-client loop used to encode for them.
    /// </summary>
    private SessionSnapshot NewBroadcastSnapshot(long now, object state, List<FrameEvent>? frameEvents) => new(
        SessionId: SessionId,
        TickId: TickId,
        ServerTimeMs: now,
        State: state,
        Forced: false,
        FrameEvents: frameEvents);

    /// <summary>
    /// True when the state's serialized length is within MaxSnapshotBytes.
    /// Measured with a reused writer over Stream.Null, which counts the bytes
    /// without materializing the state's array - the tick body only needs the
    /// length. Same predicate and same default options as the previous byte[]
    /// probe, so the cap decision is unchanged.
    /// </summary>
    private bool StateWithinCap(object state)
    {
        var probe = _stateProbe ??= new Utf8JsonWriter(Stream.Null);
        probe.Reset();
        JsonSerializer.Serialize(probe, state);
        probe.Flush();
        return probe.BytesCommitted <= _config.MaxSnapshotBytes;
    }

    private sealed record ForceInfo(long LagTicks, long LastAckTickId);

    private Dictionary<string, ForceInfo> GetClientsNeedingResync(long nowMs)
    {
        if (_config.MaxLagTicks <= 0 || _clients.Count == 0)
        {
            return new Dictionary<string, ForceInfo>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, ForceInfo>(StringComparer.Ordinal);
        foreach (var (_, state) in _clients.Values)
        {
            var lag = TickId - state.LastAckTickId;
            if (lag <= _config.MaxLagTicks)
            {
                continue;
            }

            if (state.LastResyncTimeMs > 0 && nowMs - state.LastResyncTimeMs < _config.ResyncCooldownMs)
            {
                continue;
            }

            map[state.ClientId] = new ForceInfo(lag, state.LastAckTickId);
        }

        return map;
    }

    private void UpdateDiagnostics(long lastTickProcessMs)
    {
        var now = NowMs();
        var clients = new List<SessionClientDiagnostics>(_clients.Count);
        var clientQueueDropped = 0L;
        foreach (var (client, state) in _clients.Values)
        {
            var lag = TickId - state.LastAckTickId;
            clientQueueDropped += client.Dropped;
            clients.Add(new SessionClientDiagnostics(
                ClientId: state.ClientId,
                ConnectedTickId: state.ConnectedTickId,
                LastAckTickId: state.LastAckTickId,
                LagTicks: lag,
                LastAckAgeMs: Math.Max(0, now - state.LastAckTimeMs),
                ResyncCount: state.ResyncCount,
                LastResyncAgeMs: state.LastResyncTimeMs <= 0 ? -1 : Math.Max(0, now - state.LastResyncTimeMs),
                ClientQueueDropped: client.Dropped
            ));
        }

        _diagnostics = new SessionDiagnostics(
            SessionId: SessionId,
            TickId: TickId,
            TickDurationMs: _config.TickDurationMs,
            ServerTimeMs: now,
            TotalIngestedEvents: Interlocked.Read(ref _totalIngestedEvents),
            TotalAppliedEvents: Interlocked.Read(ref _totalAppliedEvents),
            TotalMergedEvents: Interlocked.Read(ref _totalMergedEvents),
            TotalDroppedEvents: Interlocked.Read(ref _totalDroppedEvents),
            TotalSnapshotsSent: Interlocked.Read(ref _totalSnapshotsSent),
            TotalResyncSnapshotsSent: Interlocked.Read(ref _totalResyncSnapshotsSent),
            TotalAcksAccepted: Interlocked.Read(ref _totalAcksAccepted),
            TotalAcksDuplicate: Interlocked.Read(ref _totalAcksDuplicate),
            TotalAcksStale: Interlocked.Read(ref _totalAcksStale),
            TotalAcksInvalid: Interlocked.Read(ref _totalAcksInvalid),
            LastTickProcessMs: lastTickProcessMs,
            Clients: clients,
            TotalDroppedOverflow: Interlocked.Read(ref _totalDroppedOverflow),
            TotalDroppedGifts: Interlocked.Read(ref _totalDroppedGifts),
            TotalTickOverruns: Interlocked.Read(ref _totalTickOverruns),
            TotalTickErrors: Interlocked.Read(ref _totalTickErrors),
            MailboxDepth: (_control.Reader.CanCount ? _control.Reader.Count : 0) + (_events.Reader.CanCount ? _events.Reader.Count : 0),
            TotalSnapshotsOversize: Interlocked.Read(ref _totalSnapshotsOversize),
            TotalResyncRequests: Interlocked.Read(ref _totalResyncRequests),
            TotalResyncReplays: Interlocked.Read(ref _totalResyncReplays),
            TotalResyncWindowMisses: Interlocked.Read(ref _totalResyncWindowMisses),
            TotalResyncThrottled: Interlocked.Read(ref _totalResyncThrottled),
            TotalResyncReplayCapped: Interlocked.Read(ref _totalResyncReplayCapped),
            FrameHistoryBytes: _frameHistory.Bytes,
            LastSnapshotBytes: Interlocked.Read(ref _lastSnapshotBytes),
            TotalClientQueueDropped: clientQueueDropped
        );
    }

    private void RecordSnapshotBytes(WireSnapshot wire) =>
        Interlocked.Exchange(ref _lastSnapshotBytes, wire.EncodedJson.Length);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _game.Dispose();
        _stateProbe?.Dispose();
    }
}

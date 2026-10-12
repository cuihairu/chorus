using System.Threading.Channels;
using Chorus.Engine.Abstractions;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Session;

public sealed class SessionActor : IAsyncDisposable
{
    private interface ICommand;

    private sealed record AddClientCmd(ISessionClient Client) : ICommand;
    private sealed record RemoveClientCmd(string ClientId) : ICommand;
    private sealed record AckCmd(string ClientId, long LastAppliedTickId, long ClientTimeMs) : ICommand;
    private sealed record IngestAudienceEventCmd(AudienceEvent Event) : ICommand;

    private readonly Channel<ICommand> _mailbox = Channel.CreateUnbounded<ICommand>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly Dictionary<string, (ISessionClient Client, SessionClientState State)> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<AudienceEvent>> _scheduledByTick = new();

    private readonly SessionConfig _config;
    private readonly IGameModule _game;
    private readonly IAudienceEventReducer _reducer;
    private volatile SessionDiagnostics _diagnostics;

    private long _totalIngestedEvents;
    private long _totalAppliedEvents;
    private long _totalMergedEvents;
    private long _totalDroppedEvents;
    private long _totalSnapshotsSent;
    private long _totalResyncSnapshotsSent;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public string SessionId { get; }
    public long TickId { get; private set; }

    public SessionActor(
        string sessionId,
        SessionConfig config,
        IGameModule game,
        IAudienceEventReducer? reducer = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("SessionId is required.", nameof(sessionId));
        }

        SessionId = sessionId;
        _config = config;
        _game = game;
        _reducer = reducer ?? new DefaultAudienceEventReducer();

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
        }
    }

    public ValueTask AddClientAsync(ISessionClient client)
        => _mailbox.Writer.WriteAsync(new AddClientCmd(client));

    public ValueTask RemoveClientAsync(string clientId)
        => _mailbox.Writer.WriteAsync(new RemoveClientCmd(clientId));

    public ValueTask AckAsync(string clientId, long lastAppliedTickId, long clientTimeMs)
        => _mailbox.Writer.WriteAsync(new AckCmd(clientId, lastAppliedTickId, clientTimeMs));

    public ValueTask IngestAudienceEventAsync(AudienceEvent e)
        => _mailbox.Writer.WriteAsync(new IngestAudienceEventCmd(e));

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        if (_timer == null)
        {
            throw new InvalidOperationException("Start() must be called before running.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            Task<bool> tickTask;
            try
            {
                tickTask = _timer.WaitForNextTickAsync(cancellationToken).AsTask();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            var readTask = _mailbox.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(tickTask, readTask).ConfigureAwait(false);

            if (completed == readTask)
            {
                while (_mailbox.Reader.TryRead(out var cmd))
                {
                    await HandleCommandAsync(cmd, cancellationToken).ConfigureAwait(false);
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

            if (!ticked)
            {
                break;
            }

            while (_mailbox.Reader.TryRead(out var cmd))
            {
                await HandleCommandAsync(cmd, cancellationToken).ConfigureAwait(false);
            }

            await OnTickAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask HandleCommandAsync(ICommand cmd, CancellationToken cancellationToken)
    {
        switch (cmd)
        {
            case AddClientCmd c:
                {
                    var now = NowMs();
                    _clients[c.Client.ClientId] = (c.Client, new SessionClientState
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
                _clients.Remove(c.ClientId);
                break;
            case AckCmd c:
                if (_clients.TryGetValue(c.ClientId, out var item))
                {
                    item.State.LastAckTickId = c.LastAppliedTickId;
                    item.State.LastAckTimeMs = NowMs();
                }
                break;
            case IngestAudienceEventCmd c:
                ScheduleAudienceEvent(c.Event);
                break;
            default:
                throw new InvalidOperationException($"Unknown command: {cmd.GetType().Name}");
        }

        return ValueTask.CompletedTask;
    }

    private void ScheduleAudienceEvent(AudienceEvent e)
    {
        Interlocked.Increment(ref _totalIngestedEvents);
        var targetTick = TickId + _config.InputDelayTicks;
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
        TickId++;

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

            var reduced = _reducer.Reduce(events, _config.MaxEventsPerTick);
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

        var now = NowMs();
        var needsPeriodic = _config.SnapshotEveryTicks > 0 && (TickId % _config.SnapshotEveryTicks) == 0;
        var forceClients = GetClientsNeedingResync(now);
        if (needsPeriodic || forceClients.Count > 0)
        {
            var state = _game.GetStateSnapshot();
            if (needsPeriodic)
            {
                foreach (var (client, stateInfo) in _clients.Values)
                {
                    var force = forceClients.TryGetValue(stateInfo.ClientId, out var forceInfo);
                    var snapshot = new SessionSnapshot(
                        SessionId: SessionId,
                        TickId: TickId,
                        ServerTimeMs: now,
                        State: state,
                        Forced: force ? true : false,
                        Reason: force ? "lag" : null,
                        TargetClientId: force ? stateInfo.ClientId : null,
                        ClientLagTicks: force ? forceInfo!.LagTicks : null,
                        ClientLastAckTickId: force ? forceInfo!.LastAckTickId : null
                    );
                    await client.SendSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _totalSnapshotsSent);
                    if (force)
                    {
                        stateInfo.LastResyncTimeMs = now;
                        stateInfo.ResyncCount++;
                        Interlocked.Increment(ref _totalResyncSnapshotsSent);
                    }
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
                        ClientLastAckTickId: forceInfo.LastAckTickId
                    );
                    await client.SendSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _totalSnapshotsSent);
                    stateInfo.LastResyncTimeMs = now;
                    stateInfo.ResyncCount++;
                    Interlocked.Increment(ref _totalResyncSnapshotsSent);
                }
            }
        }

        sw.Stop();
        UpdateDiagnostics(sw.ElapsedMilliseconds);
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
        foreach (var (_, state) in _clients.Values)
        {
            var lag = TickId - state.LastAckTickId;
            clients.Add(new SessionClientDiagnostics(
                ClientId: state.ClientId,
                ConnectedTickId: state.ConnectedTickId,
                LastAckTickId: state.LastAckTickId,
                LagTicks: lag,
                LastAckAgeMs: Math.Max(0, now - state.LastAckTimeMs),
                ResyncCount: state.ResyncCount,
                LastResyncAgeMs: state.LastResyncTimeMs <= 0 ? -1 : Math.Max(0, now - state.LastResyncTimeMs)
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
            LastTickProcessMs: lastTickProcessMs,
            Clients: clients
        );
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _game.Dispose();
    }
}

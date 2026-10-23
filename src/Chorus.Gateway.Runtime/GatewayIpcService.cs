using System.Collections.Concurrent;
using Chorus.Control;
using Chorus.Engine.Routing;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Messages;

namespace Chorus.Gateway;

public sealed partial class GatewayIpcService : BackgroundService
{
    private readonly ILogger<GatewayIpcService> _logger;
    private readonly IConfiguration _config;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GatewayClientConnection>> _clientsBySession = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerWelcome>> _pendingWelcomes = new(StringComparer.Ordinal);

    private readonly string[] _workerIds;
    private IMessageBus? _incoming;
    private IMessageBus? _outgoing;
    private IReadOnlyDictionary<string, IMessageBus> _outgoingByWorker = new Dictionary<string, IMessageBus>(StringComparer.Ordinal);
    private IpcSettings _ipcSettings;
    private long _publishFailures;
    private long _lastEngineMessageMs;
    private readonly int _readinessMaxSilenceMs;

    /// <summary>Failed outgoing IPC publishes (chorus_ipc_publish_failures_total).</summary>
    public long PublishFailures => Interlocked.Read(ref _publishFailures);

    /// <summary>
    /// Readiness gate (docs/reference/deployment.md "split-mode readiness probe"):
    /// the engine is considered reachable while messages (heartbeats, welcomes,
    /// snapshots) keep arriving within the configured silence window. Never
    /// having received one is also not-ready - fail-closed before first contact.
    /// </summary>
    public bool IsEngineReady(long nowMs)
    {
        if (_control is not null) return _controlReady
            && Volatile.Read(ref _controlRoutingSnapshot)?.CanServeRooms(nowMs) == true;
        if (_workerIds.Length > 0) return _workerIds.All(id => _workerLastSeen.TryGetValue(id,out var at) && nowMs - at <= _readinessMaxSilenceMs);
        var last = Interlocked.Read(ref _lastEngineMessageMs);
        return last != 0 && nowMs - last <= _readinessMaxSilenceMs;
    }

    /// <summary>Milliseconds since the last engine message; -1 before the first one.</summary>
    public long EngineSilenceMs(long nowMs)
    {
        var last = Interlocked.Read(ref _lastEngineMessageMs);
        return last == 0 ? -1 : Math.Max(0, nowMs - last);
    }

    /// <summary>Records engine-channel liveness. Called by the receive loop; tests call it directly.</summary>
    public void RecordEngineMessage(long nowMs) => Interlocked.Exchange(ref _lastEngineMessageMs, nowMs);

    public GatewayIpcService(ILogger<GatewayIpcService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;

        _ipcSettings = new IpcSettings(
            Transport: Enum.TryParse<IpcTransport>(_config["Ipc:Transport"], ignoreCase: true, out var t) ? t : IpcTransport.Auto,
            UnixSocketDir: _config["Ipc:UnixSocketDir"] ?? "/tmp/chorus",
            NamedPipePrefix: _config["Ipc:NamedPipePrefix"] ?? "chorus",
            TcpHost: _config["Ipc:TcpHost"] ?? "127.0.0.1",
            TcpGatewayToEnginePort: int.TryParse(_config["Ipc:TcpGatewayToEnginePort"], out var p1) ? p1 : 27500,
            TcpEngineToGatewayPort: int.TryParse(_config["Ipc:TcpEngineToGatewayPort"], out var p2) ? p2 : 27501,
            HttpGatewayToEngineUrl: _config["Ipc:HttpGatewayToEngineUrl"] ?? "http://127.0.0.1:27600/gw-to-eng/",
            HttpEngineToGatewayUrl: _config["Ipc:HttpEngineToGatewayUrl"] ?? "http://127.0.0.1:27601/eng-to-gw/",
            // This gateway's own identity: suffixes its eng->gw endpoint so
            // several gateways can share one worker (stage-2 transport half).
            GatewayId: _config["Gateway:Id"] ?? "",
            // Eng->gw shard count shared with the engine (Ipc:EngToGwShards):
            // the gateway hosts one server socket per shard.
            EngineToGatewayShards: int.TryParse(_config["Ipc:EngToGwShards"], out var engToGwShards) ? Math.Max(1, engToGwShards) : 1
        );

        _ipcSettings = _ipcSettings with
        {
            Workers = _config.GetSection("Ipc:Workers").Get<Dictionary<string, IpcNetworkEndpoint>>(),
            Gateways = _config.GetSection("Ipc:Gateways").Get<Dictionary<string, IpcNetworkEndpoint>>(),
        };

        _workerIds = (_config["Ipc:WorkerIds"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(_config["Control:Url"]))
        {
            if (string.IsNullOrWhiteSpace(_ipcSettings.GatewayId)) throw new ArgumentException("Control mode requires Gateway:Id.");
            _control = new ControlClient(_config,_ipcSettings.GatewayId);
            _machineId = _config["Control:MachineId"] ?? Environment.MachineName;
        }

        // Recommend >= 3x the engine heartbeat (default 5000) - see deployment.md.
        _readinessMaxSilenceMs = int.TryParse(_config["Ipc:ReadinessMaxSilenceMs"], out var w) ? Math.Max(1, w) : 15000;
    }

    public Task<ServerWelcome> CreateWelcomeWaiter(string sessionId, string clientId, CancellationToken cancellationToken)
    {
        var key = $"{sessionId}|{clientId}";
        var tcs = new TaskCompletionSource<ServerWelcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingWelcomes.TryAdd(key, tcs))
        {
            throw new InvalidOperationException("Welcome waiter already exists.");
        }

        cancellationToken.Register(() =>
        {
            if (_pendingWelcomes.TryRemove(key, out var removed))
            {
                removed.TrySetCanceled(cancellationToken);
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// Releases a parked welcome waiter when the registration window closes
    /// without a welcome (timeout / failed register). Deterministic: the
    /// cancel-registration alone races the caller's CTS disposal and can be
    /// skipped, and a parked waiter would reject every later reconnect of the
    /// same (session, client) - 2026-10-15 gate run reconnect storm.
    /// </summary>
    public void TryCancelWelcomeWaiter(string sessionId, string clientId)
    {
        if (_pendingWelcomes.TryRemove($"{sessionId}|{clientId}", out var tcs))
        {
            tcs.TrySetCanceled();
        }
    }

    public async ValueTask RegisterClientAsync(GatewayClientConnection conn, CancellationToken cancellationToken)
    {
        var map = _clientsBySession.GetOrAdd(conn.SessionId, _ => new ConcurrentDictionary<string, GatewayClientConnection>(StringComparer.Ordinal));
        if (_control is not null) await _control.Send("connections/connect",Connection(conn),cancellationToken);
        map[conn.ClientId] = conn;

        await PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayConnect, conn.SessionId, new GatewayConnectCmd(conn.SessionId, conn.ClientId,_ipcSettings.GatewayId,_control?.InstanceId,conn.Generation)),
            cancellationToken);
    }

    public async ValueTask UnregisterClientAsync(string sessionId, string clientId, CancellationToken cancellationToken, string? generation = null)
    {
        GatewayClientConnection? removed = null;
        if (_clientsBySession.TryGetValue(sessionId, out var map))
        {
            if (map.TryGetValue(clientId,out var current) && generation is not null && current.Generation != generation) return;
            map.TryRemove(clientId, out removed);
            if (map.IsEmpty)
            {
                _clientsBySession.TryRemove(sessionId, out _);
            }
        }

        if (_control is not null && removed is not null) await _control.Send("connections/disconnect",Connection(removed),cancellationToken);
        await PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayDisconnect, sessionId, new GatewayDisconnectCmd(sessionId, clientId,_ipcSettings.GatewayId,_control?.InstanceId,generation ?? removed?.Generation)),
            cancellationToken);
    }

    public ValueTask PublishAckAsync(string sessionId, string clientId, long lastAppliedTickId, long clientTimeMs, CancellationToken cancellationToken)
        => PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayAck, sessionId, new GatewayAckCmd(sessionId, clientId, lastAppliedTickId, clientTimeMs)),
            cancellationToken);

    public ValueTask PublishResyncAsync(string sessionId, string clientId, long fromFrameId, CancellationToken cancellationToken)
        => PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayResync, sessionId, new GatewayResyncCmd(sessionId, clientId, fromFrameId)),
            cancellationToken);

    public ValueTask PublishAudienceEventAsync(Envelope env, CancellationToken cancellationToken)
        => PublishAsync(env, cancellationToken);

    public string? RouteWorker(string sessionId)
        => _workerIds.Length == 0 ? null : RoomAffinity.Assign(sessionId, _workerIds);

    private ValueTask PublishAsync(Envelope env, CancellationToken cancellationToken)
    {
        if (_control is not null) return PublishControlledAsync(env,cancellationToken);
        if (_workerIds.Length == 0)
        {
            if (_outgoing == null)
            {
                throw new InvalidOperationException("Outgoing bus not ready.");
            }

            return SafePublishAsync(env, cancellationToken);
        }

        var workerId = RouteWorker(env.SessionId);
        if (workerId is null || !_outgoingByWorker.TryGetValue(workerId, out var outgoing))
        {
            throw new InvalidOperationException($"No engine channel for session '{env.SessionId}'.");
        }

        return SafePublishToAsync(outgoing, env, cancellationToken);
    }

    private async ValueTask SafePublishAsync(Envelope env, CancellationToken cancellationToken)
        => await SafePublishToAsync(_outgoing!, env, cancellationToken);

    private async ValueTask SafePublishToAsync(IMessageBus outgoing, Envelope env, CancellationToken cancellationToken)
    {
        try
        {
            await outgoing.PublishAsync(env, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _publishFailures);
            _logger.LogWarning(ex, "IPC publish failed type={Type} session={SessionId} message={MessageId}", env.Type, env.SessionId, env.MessageId);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoints = IpcEndpointBuilder.Build(_ipcSettings);
        // One eng->gw server per shard (Ipc:EngToGwShards, todo §10 blocker ②):
        // the engine publishes each session's frames to the shard its id
        // hashes to; here every shard socket gets its own reader and
        // FanInMessageBus merges them back into the single-consumer loop
        // below, so dispatch semantics stay exactly as before. Shard count 1
        // keeps the legacy single-server path untouched.
        var shardCount = Math.Max(1, _ipcSettings.EngineToGatewayShards);
        var incomingShards = Enumerable.Range(0, shardCount)
            .Select(shard => IpcMessageBusFactory.CreateServer(
                _ipcSettings.Transport,
                IpcEndpointBuilder.Build(
                    _ipcSettings with { EngineToGatewayShardIndex = shard }, forServer: true).EngineToGateway))
            .ToArray();
        _incoming = incomingShards.Length == 1
            ? incomingShards[0]
            : new FanInMessageBus(incomingShards);

        if (_control is not null) _controlTask = RunControlAsync(stoppingToken);
        if (_workerIds.Length == 0)
        {
            _outgoing = IpcMessageBusFactory.CreateClient(_ipcSettings.Transport, endpoints.GatewayToEngine);
        }
        else
        {
            var outgoingByWorker = new Dictionary<string, IMessageBus>(StringComparer.Ordinal);
            foreach (var workerId in _workerIds)
            {
                var workerEndpoints = IpcEndpointBuilder.Build(_ipcSettings with { WorkerId = workerId });
                outgoingByWorker[workerId] = IpcMessageBusFactory.CreateClient(
                    _ipcSettings.Transport,
                    workerEndpoints.GatewayToEngine);
            }

            _outgoingByWorker = outgoingByWorker;
        }

        _logger.LogInformation(
            "Gateway IPC started: transport={Transport} eng->gw={EngToGwUds} engToGwShards={EngToGwShards} gw->eng={GwToEngUds} workerIds={WorkerIds}",
            _ipcSettings.Transport,
            endpoints.EngineToGateway.UnixSocketPath,
            shardCount,
            endpoints.GatewayToEngine.UnixSocketPath,
            string.Join(",", _workerIds));

        await foreach (var env in _incoming.SubscribeAsync(stoppingToken))
        {
            RecordEngineMessage(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (env.Type == IpcMessageTypes.EngineHeartbeat)
            {
                var heartbeat = EnvelopeFactory.DeserializeBody<EngineHeartbeatCmd>(env);
                if (!string.IsNullOrEmpty(heartbeat.WorkerId)) _workerLastSeen[heartbeat.WorkerId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                continue;
            }
            if (_control is not null)
            {
                var route=Volatile.Read(ref _controlRoutingSnapshot);
                if (route is null || !route.TryGetAuthority(env.SessionId,env.AuthorityEpoch,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),out _)) continue;
            }

            if (env.Type == IpcMessageTypes.EngineWelcome)
            {
                try
                {
                    var welcome = EnvelopeFactory.DeserializeBody<ServerWelcome>(env);
                    var key = $"{welcome.SessionId}|{welcome.ClientId}";
                    if (_pendingWelcomes.TryRemove(key, out var tcs))
                    {
                        tcs.TrySetResult(welcome);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to handle engine welcome session={SessionId}", env.SessionId);
                }
            }
            else if (env.Type == IpcMessageTypes.EngineSnapshot)
            {
                try
                {
                    var snapshot = EnvelopeFactory.DeserializeBody<ServerSnapshot>(env);
                    await DispatchSnapshotAsync(snapshot, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispatch snapshot session={SessionId}", env.SessionId);
                }
            }
        }
    }

    private async Task DispatchSnapshotAsync(ServerSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_clientsBySession.TryGetValue(snapshot.SessionId, out var clients) || clients.IsEmpty)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.TargetClientId))
        {
            if (clients.TryGetValue(snapshot.TargetClientId, out var conn))
            {
                await conn.SendSnapshotAsync(snapshot, cancellationToken);
            }
            return;
        }

        foreach (var c in clients.Values)
        {
            await c.SendSnapshotAsync(snapshot, cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _controlStop.Cancel();
        try { await _controlTask; } catch (OperationCanceledException) { }
        foreach (var key in _pendingWelcomes.Keys)
        {
            if (_pendingWelcomes.TryRemove(key, out var tcs))
            {
                tcs.TrySetCanceled(cancellationToken);
            }
        }

        if (_incoming != null) await _incoming.DisposeAsync();
        if (_outgoing != null)
        {
            await _outgoing.DisposeAsync();
        }

        foreach (var outgoing in _outgoingByWorker.Values)
        {
            await outgoing.DisposeAsync();
        }

        _outgoingByWorker = new Dictionary<string, IMessageBus>(StringComparer.Ordinal);
        foreach (var bus in _controlChannels.Values) await bus.DisposeAsync();
        _controlChannels.Clear();
        await base.StopAsync(cancellationToken);
    }
}

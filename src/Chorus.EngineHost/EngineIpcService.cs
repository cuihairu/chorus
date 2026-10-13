using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Models;
using Chorus.Protocol.Messages;

namespace Chorus.EngineHost;

public sealed class EngineIpcService : BackgroundService
{
    private readonly ILogger<EngineIpcService> _logger;
    private readonly IGameModuleFactory _gameFactory;
    private readonly SessionConfig _sessionConfig;
    private readonly IpcSettings _ipcSettings;

    private readonly SessionRegistry _registry;
    private readonly Dictionary<(string SessionId, string ClientId), EngineBusSessionClient> _clients = new();
    private readonly BroadcastOwnerRegistry _broadcastOwners = new();
    private long _lastSweepMs;

    private IMessageBus? _incoming;
    private IMessageBus? _outgoing;
    private readonly string[] _gatewayIds;

    public EngineIpcService(
        ILogger<EngineIpcService> logger,
        IGameModuleFactory gameFactory,
        SessionConfig sessionConfig,
        IConfiguration config)
    {
        _logger = logger;
        _gameFactory = gameFactory;
        _sessionConfig = sessionConfig;

        // Session lifecycle (cap, idle eviction, sweep) is shared with the
        // monolith's SessionManager. The sweep loop stays off: the engine
        // sweeps amortized on session creation, on the IPC receive loop
        // thread, keeping all registry mutations single-threaded with the
        // connect/ack/event handlers (the client probe below reads _clients).
        _registry = new SessionRegistry(
            sessionConfig,
            CreateActor,
            id => _clients.Keys.Any(k => k.SessionId == id),
            runSweepLoop: false);

        _ipcSettings = new IpcSettings(
            Transport: Enum.TryParse<IpcTransport>(config["Ipc:Transport"], ignoreCase: true, out var t) ? t : IpcTransport.Auto,
            UnixSocketDir: config["Ipc:UnixSocketDir"] ?? "/tmp/chorus",
            NamedPipePrefix: config["Ipc:NamedPipePrefix"] ?? "chorus",
            TcpHost: config["Ipc:TcpHost"] ?? "127.0.0.1",
            TcpGatewayToEnginePort: int.TryParse(config["Ipc:TcpGatewayToEnginePort"], out var p1) ? p1 : 27500,
            TcpEngineToGatewayPort: int.TryParse(config["Ipc:TcpEngineToGatewayPort"], out var p2) ? p2 : 27501,
            HttpGatewayToEngineUrl: config["Ipc:HttpGatewayToEngineUrl"] ?? "http://127.0.0.1:27600/gw-to-eng/",
            HttpEngineToGatewayUrl: config["Ipc:HttpEngineToGatewayUrl"] ?? "http://127.0.0.1:27601/eng-to-gw/"
        );

        // Liveness ping on the eng->gw channel (docs/reference/deployment.md
        // "split-mode readiness probe"): keeps the Gateway's /ready honest even
        // when no client is connected (no welcomes/snapshots flowing). 0 = off.
        _heartbeatIntervalMs = int.TryParse(config["Ipc:HeartbeatIntervalMs"], out var hb) ? Math.Max(0, hb) : 5000;

        // Comma-separated gateway identities hosting the suffixed eng->gw
        // endpoints this worker fans out to (e.g. "platform-gw,streamer-gw").
        // Empty = legacy single-gateway channel.
        _gatewayIds = (config["Ipc:GatewayIds"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    /// <summary>Per-gateway eng->gw clients wrapped in a fan-out; empty id
    /// list keeps the legacy single-gateway client untouched.</summary>
    private IMessageBus BuildOutgoing(IpcDuplexEndpoints endpoints)
    {
        if (_gatewayIds.Length == 0)
        {
            return IpcMessageBusFactory.CreateClient(_ipcSettings.Transport, endpoints.EngineToGateway);
        }

        return new FanOutMessageBus(_gatewayIds.Select(id =>
            IpcMessageBusFactory.CreateClient(
                _ipcSettings.Transport,
                IpcEndpointBuilder.Build(_ipcSettings with { GatewayId = id }).EngineToGateway)));
    }

    private readonly int _heartbeatIntervalMs;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoints = IpcEndpointBuilder.Build(_ipcSettings);

        _incoming = IpcMessageBusFactory.CreateServer(_ipcSettings.Transport, endpoints.GatewayToEngine);
        // One eng->gw channel per configured gateway (Ipc:GatewayIds, comma
        // separated); each gateway hosts its own suffixed endpoint and the
        // engine fans every message out to all of them (stage-2 transport
        // half). Empty list = legacy single-gateway channel, byte-identical.
        _outgoing = BuildOutgoing(endpoints);

        _logger.LogInformation("EngineHost IPC started: transport={Transport} gw->eng={GwToEngUds} eng->gw={EngToGwUds} gatewayIds={GatewayIds} heartbeatMs={HeartbeatMs}",
            _ipcSettings.Transport, endpoints.GatewayToEngine.UnixSocketPath, endpoints.EngineToGateway.UnixSocketPath, string.Join(",", _gatewayIds), _heartbeatIntervalMs);

        var heartbeatLoop = _heartbeatIntervalMs > 0
            ? Task.Run(() => HeartbeatLoopAsync(stoppingToken), stoppingToken)
            : Task.CompletedTask;

        await foreach (var env in _incoming.SubscribeAsync(stoppingToken))
        {
            try
            {
                await HandleAsync(env, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to handle IPC message type={Type} session={SessionId}", env.Type, env.SessionId);
            }
        }

        await heartbeatLoop;
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_heartbeatIntervalMs, stoppingToken).ConfigureAwait(false);
            try
            {
                // EnvelopeFactory rejects empty session ids; heartbeats are not
                // session-scoped, so carry the well-known sentinel instead.
                await _outgoing!.PublishAsync(
                    EnvelopeFactory.Create(
                        IpcMessageTypes.EngineHeartbeat,
                        "__heartbeat",
                        new EngineHeartbeatCmd(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Heartbeats are best-effort liveness; the Gateway flips /ready
                // to 503 on silence, so a failed beat must not crash the loop.
                _logger.LogDebug(ex, "Engine heartbeat publish failed");
            }
        }
    }

    private SessionActor CreateActor(string sessionId)
    {
        var game = _gameFactory.Create(sessionId);
        var actor = new SessionActor(sessionId, _sessionConfig, game);
        actor.Start();
        return actor;
    }

    private SessionActor GetOrCreateSession(string sessionId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Amortized lazy sweep for client-less sessions (review stage-2,
        // EngineHost topology): runs on the receive loop thread so registry
        // mutations stay single-threaded with the handlers. Cap eviction with
        // idlest-first victims happens inside the registry's GetOrCreate.
        if (now - _lastSweepMs >= 30_000 || _registry.Count >= Math.Max(1, _sessionConfig.MaxSessions))
        {
            _lastSweepMs = now;
            _ = _registry.SweepIdleAsync(now);
        }

        return _registry.GetOrCreate(sessionId, now);
    }

    private async Task HandleAsync(Envelope env, CancellationToken cancellationToken)
    {
        if (_outgoing == null)
        {
            throw new InvalidOperationException("Outgoing bus is not initialized.");
        }

        switch (env.Type)
        {
            case IpcMessageTypes.GatewayConnect:
                {
                    var cmd = EnvelopeFactory.DeserializeBody<GatewayConnectCmd>(env);
                    var session = GetOrCreateSession(cmd.SessionId);
                    _broadcastOwners.OnConnect(cmd.SessionId, cmd.ClientId);
                    var client = new EngineBusSessionClient(
                        _outgoing, cmd.SessionId, cmd.ClientId,
                        () => _broadcastOwners.IsOwner(cmd.SessionId, cmd.ClientId));
                    _clients[(cmd.SessionId, cmd.ClientId)] = client;
                    await session.AddClientAsync(client);

                    var welcome = new ServerWelcome(
                        Type: Chorus.Protocol.ProtocolTypes.Welcome,
                        SessionId: cmd.SessionId,
                        ClientId: cmd.ClientId,
                        TickDurationMs: _sessionConfig.TickDurationMs,
                        InputDelayTicks: _sessionConfig.InputDelayTicks,
                        SnapshotEveryTicks: _sessionConfig.SnapshotEveryTicks,
                        ServerTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ProtocolVersion: Chorus.Protocol.ProtocolVersionPolicy.Current
                    );
                    await _outgoing.PublishAsync(
                        EnvelopeFactory.Create(IpcMessageTypes.EngineWelcome, cmd.SessionId, welcome),
                        cancellationToken);
                }
                break;

            case IpcMessageTypes.GatewayDisconnect:
                {
                    var cmd = EnvelopeFactory.DeserializeBody<GatewayDisconnectCmd>(env);
                    if (_registry.TryGet(cmd.SessionId, out var session))
                    {
                        await session!.RemoveClientAsync(cmd.ClientId);
                    }
                    _clients.Remove((cmd.SessionId, cmd.ClientId));
                    _broadcastOwners.OnDisconnect(
                        cmd.SessionId,
                        cmd.ClientId,
                        _clients.Keys.Where(k => k.SessionId == cmd.SessionId).Select(k => k.ClientId));
                }
                break;

            case IpcMessageTypes.GatewayAck:
                {
                    var cmd = EnvelopeFactory.DeserializeBody<GatewayAckCmd>(env);
                    if (_registry.TryGet(cmd.SessionId, out var session))
                    {
                        await session!.AckAsync(cmd.ClientId, cmd.LastAppliedTickId, cmd.ClientTimeMs);
                    }
                }
                break;

            case IpcMessageTypes.GatewayAudienceEvent:
                {
                    var audienceEvent = EnvelopeFactory.DeserializeBody<AudienceEvent>(env);
                    if (!string.IsNullOrEmpty(env.SourceEventId)
                        && !string.Equals(env.SourceEventId, audienceEvent.EventId, StringComparison.Ordinal))
                    {
                        _logger.LogWarning(
                            "Correlation mismatch: envelope source={SourceEventId} event={EventId} session={SessionId} message={MessageId}",
                            env.SourceEventId, audienceEvent.EventId, audienceEvent.SessionId, env.MessageId);
                    }
                    var session = GetOrCreateSession(audienceEvent.SessionId);
                    if (!await session.TryIngestAudienceEventAsync(audienceEvent))
                    {
                        // Delivery Failed at the engine boundary; the gateway
                        // already acked the platform, so only observe here.
                        _logger.LogWarning(
                            "Event lane full, event dropped type={Type} session={SessionId} event={EventId}",
                            env.Type, audienceEvent.SessionId, audienceEvent.EventId);
                    }
                }
                break;
        }
    }

    /// <summary>Session diagnostics for the /metrics surface.</summary>
    public IReadOnlyList<SessionDiagnostics> GetDiagnostics() => _registry.GetDiagnostics();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _registry.DisposeAsync();
        _clients.Clear();

        if (_incoming != null) await _incoming.DisposeAsync();
        if (_outgoing != null) await _outgoing.DisposeAsync();

        await base.StopAsync(cancellationToken);
    }
}

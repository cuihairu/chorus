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

    private readonly Dictionary<string, SessionActor> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SessionId, string ClientId), EngineBusSessionClient> _clients = new();

    private IMessageBus? _incoming;
    private IMessageBus? _outgoing;

    public EngineIpcService(
        ILogger<EngineIpcService> logger,
        IGameModuleFactory gameFactory,
        SessionConfig sessionConfig,
        IConfiguration config)
    {
        _logger = logger;
        _gameFactory = gameFactory;
        _sessionConfig = sessionConfig;

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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoints = IpcEndpointBuilder.Build(_ipcSettings);

        _incoming = IpcMessageBusFactory.CreateServer(_ipcSettings.Transport, endpoints.GatewayToEngine);
        _outgoing = IpcMessageBusFactory.CreateClient(_ipcSettings.Transport, endpoints.EngineToGateway);

        _logger.LogInformation("EngineHost IPC started: transport={Transport} gw->eng={GwToEngUds} eng->gw={EngToGwUds}",
            _ipcSettings.Transport, endpoints.GatewayToEngine.UnixSocketPath, endpoints.EngineToGateway.UnixSocketPath);

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
    }

    private SessionActor GetOrCreateSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            return s;
        }

        var game = _gameFactory.Create(sessionId);
        var actor = new SessionActor(sessionId, _sessionConfig, game);
        actor.Start();
        _sessions[sessionId] = actor;
        return actor;
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
                    var client = new EngineBusSessionClient(_outgoing, cmd.SessionId, cmd.ClientId);
                    _clients[(cmd.SessionId, cmd.ClientId)] = client;
                    await session.AddClientAsync(client);

                    var welcome = new ServerWelcome(
                        Type: Chorus.Protocol.ProtocolTypes.Welcome,
                        SessionId: cmd.SessionId,
                        ClientId: cmd.ClientId,
                        TickDurationMs: _sessionConfig.TickDurationMs,
                        InputDelayTicks: _sessionConfig.InputDelayTicks,
                        SnapshotEveryTicks: _sessionConfig.SnapshotEveryTicks,
                        ServerTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    );
                    await _outgoing.PublishAsync(
                        EnvelopeFactory.Create(IpcMessageTypes.EngineWelcome, cmd.SessionId, welcome),
                        cancellationToken);
                }
                break;

            case IpcMessageTypes.GatewayDisconnect:
                {
                    var cmd = EnvelopeFactory.DeserializeBody<GatewayDisconnectCmd>(env);
                    if (_sessions.TryGetValue(cmd.SessionId, out var session))
                    {
                        await session.RemoveClientAsync(cmd.ClientId);
                    }
                    _clients.Remove((cmd.SessionId, cmd.ClientId));
                }
                break;

            case IpcMessageTypes.GatewayAck:
                {
                    var cmd = EnvelopeFactory.DeserializeBody<GatewayAckCmd>(env);
                    if (_sessions.TryGetValue(cmd.SessionId, out var session))
                    {
                        await session.AckAsync(cmd.ClientId, cmd.LastAppliedTickId, cmd.ClientTimeMs);
                    }
                }
                break;

            case IpcMessageTypes.GatewayAudienceEvent:
                {
                    var audienceEvent = EnvelopeFactory.DeserializeBody<AudienceEvent>(env);
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var s in _sessions.Values)
        {
            await s.DisposeAsync();
        }
        _sessions.Clear();
        _clients.Clear();

        if (_incoming != null) await _incoming.DisposeAsync();
        if (_outgoing != null) await _outgoing.DisposeAsync();

        await base.StopAsync(cancellationToken);
    }
}

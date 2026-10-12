using System.Collections.Concurrent;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Messages;

namespace Chorus.Gateway;

public sealed class GatewayIpcService : BackgroundService
{
    private readonly ILogger<GatewayIpcService> _logger;
    private readonly IConfiguration _config;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GatewayClientConnection>> _clientsBySession = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerWelcome>> _pendingWelcomes = new(StringComparer.Ordinal);

    private IMessageBus? _incoming;
    private IMessageBus? _outgoing;
    private IpcSettings _ipcSettings;
    private long _publishFailures;

    /// <summary>Failed outgoing IPC publishes (chorus_ipc_publish_failures_total).</summary>
    public long PublishFailures => Interlocked.Read(ref _publishFailures);

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
            HttpEngineToGatewayUrl: _config["Ipc:HttpEngineToGatewayUrl"] ?? "http://127.0.0.1:27601/eng-to-gw/"
        );
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

    public async ValueTask RegisterClientAsync(GatewayClientConnection conn, CancellationToken cancellationToken)
    {
        var map = _clientsBySession.GetOrAdd(conn.SessionId, _ => new ConcurrentDictionary<string, GatewayClientConnection>(StringComparer.Ordinal));
        map[conn.ClientId] = conn;

        await PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayConnect, conn.SessionId, new GatewayConnectCmd(conn.SessionId, conn.ClientId)),
            cancellationToken);
    }

    public async ValueTask UnregisterClientAsync(string sessionId, string clientId, CancellationToken cancellationToken)
    {
        if (_clientsBySession.TryGetValue(sessionId, out var map))
        {
            map.TryRemove(clientId, out _);
            if (map.IsEmpty)
            {
                _clientsBySession.TryRemove(sessionId, out _);
            }
        }

        await PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayDisconnect, sessionId, new GatewayDisconnectCmd(sessionId, clientId)),
            cancellationToken);
    }

    public ValueTask PublishAckAsync(string sessionId, string clientId, long lastAppliedTickId, long clientTimeMs, CancellationToken cancellationToken)
        => PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayAck, sessionId, new GatewayAckCmd(sessionId, clientId, lastAppliedTickId, clientTimeMs)),
            cancellationToken);

    public ValueTask PublishAudienceEventAsync(Envelope env, CancellationToken cancellationToken)
        => PublishAsync(env, cancellationToken);

    private ValueTask PublishAsync(Envelope env, CancellationToken cancellationToken)
    {
        if (_outgoing == null)
        {
            throw new InvalidOperationException("Outgoing bus not ready.");
        }

        return SafePublishAsync(env, cancellationToken);
    }

    private async ValueTask SafePublishAsync(Envelope env, CancellationToken cancellationToken)
    {
        try
        {
            await _outgoing!.PublishAsync(env, cancellationToken);
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
        _incoming = IpcMessageBusFactory.CreateServer(_ipcSettings.Transport, endpoints.EngineToGateway);
        _outgoing = IpcMessageBusFactory.CreateClient(_ipcSettings.Transport, endpoints.GatewayToEngine);

        _logger.LogInformation("Gateway IPC started: transport={Transport} eng->gw={EngToGwUds} gw->eng={GwToEngUds}",
            _ipcSettings.Transport, endpoints.EngineToGateway.UnixSocketPath, endpoints.GatewayToEngine.UnixSocketPath);

        await foreach (var env in _incoming.SubscribeAsync(stoppingToken))
        {
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
        foreach (var key in _pendingWelcomes.Keys)
        {
            if (_pendingWelcomes.TryRemove(key, out var tcs))
            {
                tcs.TrySetCanceled(cancellationToken);
            }
        }

        if (_incoming != null) await _incoming.DisposeAsync();
        if (_outgoing != null) await _outgoing.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}

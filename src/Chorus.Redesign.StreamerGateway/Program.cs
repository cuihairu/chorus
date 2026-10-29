using System.Net;
using System.Security.Cryptography.X509Certificates;
using Chorus.Redesign.Rpc;
using Chorus.Redesign.StreamerGateway;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var builder = WebApplication.CreateBuilder(args);
var clusterId = Required("CHORUS_CLUSTER_ID");
var root = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_ROOT_CA_PEM")));
var nodeAuthority = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_NODE_CA_PEM")));
var serverCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_TLS_CERT_PEM")),
    File.ReadAllText(Required("CHORUS_TLS_KEY_PEM")));
var trustedCertificates = new TrustedProcessCertificates();
var port = int.Parse(Environment.GetEnvironmentVariable("CHORUS_PORT") ?? "7530");
// The public WSS listener (operations 1.3: streamer WSS 443 → 7443). HTTP/1.1 only — the
// WebSocket client protocol the streamer clients speak, with no extended-connect surprises.
var wssPort = int.Parse(Environment.GetEnvironmentVariable("CHORUS_WSS_PORT") ?? "7443");

builder.WebHost.ConfigureKestrel(options =>
{
    // Two listeners, two trust models (operations 1.3). The internal port speaks cluster
    // mTLS gRPC: callers are processes with certificates the node authority signed. The
    // public port speaks client WSS over TLS with the server certificate only — players
    // hold no client certificates, and their identity arrives inside the protocol (the
    // platform launch credential at login, resume secrets afterwards), never at the
    // transport layer. One server certificate serves both.
    options.Listen(IPAddress.Any, port, listen =>
    {
        listen.Protocols = HttpProtocols.Http2;
        listen.UseHttps(https =>
        {
            https.ServerCertificate = serverCertificate;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) =>
                trustedCertificates.ValidateAndRemember(certificate, root, clusterId, [nodeAuthority]);
        });
    });
    options.Listen(IPAddress.Any, wssPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http1;
        listen.UseHttps(https => https.ServerCertificate = serverCertificate);
    });
});

// No database credentials on this host (architecture: the gateway holds no long-lived
// entities) — every fact it needs it asks for over the data-plane channels.
var identity = ProcessCertificateValidator.Validate(serverCertificate, root, clusterId, [nodeAuthority])
    ?? throw new InvalidOperationException("The gateway certificate carries no process identity.");

// The tenant an app belongs to is the only way a cluster-scoped gateway can name a tenant:
// one certificate serves every tenant's clients, so the map must be unambiguous at startup.
var tenantApplications = new TenantApplicationMap(Required("CHORUS_TENANT_APPLICATIONS"));
var edgeOptions = EdgeOptions.FromEnvironment();
var registry = new EdgeSessionRegistry(edgeOptions.MaxConnections);
// The StreamerSession boundary carries the login round-trips; the gateway holds no session
// facts of its own beyond what a login just returned (architecture: no long-lived entities).
var streamerPort = int.Parse(Environment.GetEnvironmentVariable("CHORUS_STREAMER_PORT") ?? "7502");
var streamerEndpoint = Environment.GetEnvironmentVariable("CHORUS_STREAMER_ENDPOINT")
    ?? $"https://localhost:{streamerPort}";
var sessionClient = new EdgeSessionClient(
    () => ProcessGrpcClientFactory.Create(new Uri(streamerEndpoint), serverCertificate,
        root, nodeAuthority, clusterId, "streamer-server"),
    identity.ProcessInstanceId);
// The MatchQueue boundary (DataServer) carries the mount's ticket; the routing watch supplies
// the room facts that turn a committed ticket into a visible match.
var dataPort = int.Parse(Environment.GetEnvironmentVariable("CHORUS_DATA_PORT") ?? "7501");
var dataEndpoint = Environment.GetEnvironmentVariable("CHORUS_DATA_ENDPOINT")
    ?? $"https://localhost:{dataPort}";
var queueClient = new EdgeQueueClient(
    () => ProcessGrpcClientFactory.Create(new Uri(dataEndpoint), serverCertificate,
        root, nodeAuthority, clusterId, "data-server"));
var controlEndpoint = Environment.GetEnvironmentVariable("CHORUS_CONTROL_ENDPOINT");
EdgeRoutingMonitor? routingMonitor = null;
if (controlEndpoint is { Length: > 0 })
{
    routingMonitor = new EdgeRoutingMonitor(
        () => ProcessGrpcClientFactory.Create(new Uri(controlEndpoint), serverCertificate,
            root, nodeAuthority, clusterId, "control-server"));
}
// The round store names the facts a room's fan-out needs (epoch, status, participation);
// the room's owning game host serves the sync stream itself, reached through a per-endpoint
// channel pool that releases a host when its last room stream ends.
var roundClient = new EdgeRoundClient(
    () => ProcessGrpcClientFactory.Create(new Uri(dataEndpoint), serverCertificate,
        root, nodeAuthority, clusterId, "data-server"));
var gameChannels = new EdgeGameChannelPool(
    endpoint => ProcessGrpcClientFactory.Create(new Uri(endpoint), serverCertificate,
        root, nodeAuthority, clusterId, "game-server"));
EdgeRoomFanout? fanout = null;
if (routingMonitor is not null)
{
    fanout = new EdgeRoomFanout((request, cancellation) =>
    {
        var channel = gameChannels.Acquire(request.Endpoint);
        return EdgeRoundClient.SubscribeStream(channel, new SubscribeRequest
        {
            RoomId = request.RoomId,
            GameId = request.GameId,
            Epoch = request.Epoch,
            LastAppliedTick = request.LastAppliedTick,
            LastAppliedStateVersion = request.LastAppliedStateVersion
        }, cancellation);
    })
    {
        ResolveRoom = async (roomId, cancellation) =>
        {
            var route = routingMonitor.RoomRouteFor(roomId);
            if (route is null) return null;
            var round = await roundClient.GetRoundAsync(route.TenantId, roomId, route.GameId, cancellation);
            return round is null
                ? null
                : new EdgeRoomFanout.ResolvedRoom(route.GameId, route.Endpoint, round.Epoch, round.Status);
        },
        StreamClosed = gameChannels.Release
    };
}
var edgeWs = new EdgeWs(edgeOptions, registry, tenantApplications, sessionClient,
    identity.ProcessInstanceId, queueClient, routingMonitor, roundClient, fanout);

// Instance self-registration (architecture 4.4): the gateway reports its internal gRPC
// endpoint — routing consumers are cluster processes, never public clients. The same control
// endpoint feeds the routing watch that pushes match notices.
if (controlEndpoint is { Length: > 0 })
{
    var reportEndpoint = Environment.GetEnvironmentVariable("CHORUS_REPORT_ENDPOINT")
        ?? $"https://localhost:{port}";
    var slotId = Environment.GetEnvironmentVariable("CHORUS_SLOT_ID") ?? $"{identity.NodeId}/{identity.Role}";
    var reportIntervalMs = int.Parse(Environment.GetEnvironmentVariable("CHORUS_REPORT_INTERVAL_MS") ?? "2000");
    builder.Services.AddHostedService(_ => new ProcessSelfReporter(
        serverCertificate, root, nodeAuthority, identity, controlEndpoint, reportEndpoint, slotId, reportIntervalMs));
    builder.Services.AddHostedService(_ => new RoutingMonitorHost(routingMonitor!));
}

builder.Services.AddSingleton(trustedCertificates);
builder.Services.AddSingleton(registry);
builder.Services.AddGrpc();

var app = builder.Build();
app.UseWebSockets();
app.MapGrpcService<GatewayGrpcService>();
app.Map("/ws", edgeWs.HandleAsync);
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before starting the gateway.");

/// <summary>The routing watch as a hosted service: it lives exactly as long as the host.</summary>
internal sealed class RoutingMonitorHost(EdgeRoutingMonitor monitor)
    : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => monitor.RunAsync(stoppingToken);
}

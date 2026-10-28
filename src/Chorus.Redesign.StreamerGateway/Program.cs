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
var edgeWs = new EdgeWs(edgeOptions, registry, identity.ProcessInstanceId);

// Instance self-registration (architecture 4.4): the gateway reports its internal gRPC
// endpoint — routing consumers are cluster processes, never public clients.
var controlEndpoint = Environment.GetEnvironmentVariable("CHORUS_CONTROL_ENDPOINT");
if (controlEndpoint is { Length: > 0 })
{
    var reportEndpoint = Environment.GetEnvironmentVariable("CHORUS_REPORT_ENDPOINT")
        ?? $"https://localhost:{port}";
    var slotId = Environment.GetEnvironmentVariable("CHORUS_SLOT_ID") ?? $"{identity.NodeId}/{identity.Role}";
    var reportIntervalMs = int.Parse(Environment.GetEnvironmentVariable("CHORUS_REPORT_INTERVAL_MS") ?? "2000");
    builder.Services.AddHostedService(_ => new ProcessSelfReporter(
        serverCertificate, root, nodeAuthority, identity, controlEndpoint, reportEndpoint, slotId, reportIntervalMs));
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

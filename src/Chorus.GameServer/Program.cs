using System.Net;
using System.Security.Cryptography.X509Certificates;
using Chorus.GameServer;
using Chorus.Redesign.Rpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var builder = WebApplication.CreateBuilder(args);
var clusterId = Required("CHORUS_CLUSTER_ID");
var root = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_ROOT_CA_PEM")));
var nodeAuthority = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_NODE_CA_PEM")));
var serverCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_TLS_CERT_PEM")),
    File.ReadAllText(Required("CHORUS_TLS_KEY_PEM")));
var trustedCertificates = new TrustedProcessCertificates();
var port = int.Parse(Environment.GetEnvironmentVariable("CHORUS_PORT") ?? "7503");
var dataPort = int.Parse(Environment.GetEnvironmentVariable("CHORUS_DATA_PORT") ?? "7501");
var dataEndpoint = Environment.GetEnvironmentVariable("CHORUS_DATA_ENDPOINT")
    ?? $"https://localhost:{dataPort}";

builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, port, listen =>
{
    listen.Protocols = HttpProtocols.Http2;
    listen.UseHttps(https =>
    {
        https.ServerCertificate = serverCertificate;
        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        https.ClientCertificateValidation = (certificate, _, _) =>
            trustedCertificates.ValidateAndRemember(certificate, root, clusterId, [nodeAuthority]);
    });
}));

// No database credentials on this host (architecture 9.3): every persistent fact lives behind
// the data-plane channel. The host serves under a cluster certificate but speaks for a tenant
// on the data plane, so the client identity is a separate tenant-scoped certificate when one
// is provisioned — the data plane sees the tenant without a request field.
var dataIdentity = serverCertificate;
var clientCertPath = Environment.GetEnvironmentVariable("CHORUS_CLIENT_CERT_PEM");
var clientKeyPath = Environment.GetEnvironmentVariable("CHORUS_CLIENT_KEY_PEM");
if (clientCertPath is { Length: > 0 } && clientKeyPath is { Length: > 0 })
    dataIdentity = X509Certificate2.CreateFromPem(File.ReadAllText(clientCertPath),
        File.ReadAllText(clientKeyPath));
Grpc.Net.Client.GrpcChannel DataChannel() => ProcessGrpcClientFactory.Create(
    new Uri(dataEndpoint), dataIdentity, root, nodeAuthority, clusterId, "data-server");

builder.Services.AddSingleton(trustedCertificates);
builder.Services.AddSingleton(new RoomRuntime(DataChannel()));
// Instance self-registration (architecture 4.4): without a supervising node agent, the host
// tells the control plane itself where it serves, so routing can reach it. The certificate
// is the identity — the report claims exactly what it proves.
var controlEndpoint = Environment.GetEnvironmentVariable("CHORUS_CONTROL_ENDPOINT");
if (controlEndpoint is { Length: > 0 })
{
    var identity = ProcessCertificateValidator.Validate(serverCertificate, root, clusterId, [nodeAuthority])
        ?? throw new InvalidOperationException("The GameServer certificate carries no process identity.");
    var reportEndpoint = Environment.GetEnvironmentVariable("CHORUS_REPORT_ENDPOINT")
        ?? $"https://localhost:{port}";
    var slotId = Environment.GetEnvironmentVariable("CHORUS_SLOT_ID") ?? $"{identity.NodeId}/{identity.Role}";
    var reportIntervalMs = int.Parse(Environment.GetEnvironmentVariable("CHORUS_REPORT_INTERVAL_MS") ?? "2000");
    var runtime = builder.Services.Single(service =>
        service.ServiceType == typeof(RoomRuntime)).ImplementationInstance as RoomRuntime
        ?? throw new InvalidOperationException("The room runtime was not registered.");
    builder.Services.AddHostedService(_ => new ProcessSelfReporter(
        serverCertificate, root, nodeAuthority, identity, controlEndpoint, reportEndpoint, slotId,
        reportIntervalMs, roomProgress: () => runtime.RoomStatuses().Select(entry => new RoomProgressReport(
            entry.RoomId, entry.Status.Epoch, entry.Status.State.ToString(), entry.Status.LastCommittedTick,
            entry.Status.LastCommittedAt?.ToUnixTimeMilliseconds(), entry.Status.StopReason)).ToList()));
}
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<GameHostGrpcService>();
app.MapGrpcService<RoundSyncGrpcService>();
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before starting GameServer.");

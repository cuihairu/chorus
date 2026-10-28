using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Chorus.StreamerServer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var builder = WebApplication.CreateBuilder(args);
var clusterId = Required("CHORUS_CLUSTER_ID");
var root = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_ROOT_CA_PEM")));
var nodeAuthority = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_NODE_CA_PEM")));
var serverCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_TLS_CERT_PEM")),
    File.ReadAllText(Required("CHORUS_TLS_KEY_PEM")));
var trustedCertificates = new TrustedProcessCertificates();
var port = int.Parse(Environment.GetEnvironmentVariable("CHORUS_PORT") ?? "7502");
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

// Tenant application mapping: { "tenant-a": { "app-1": "game-a" } }
var tenantApplications = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
    Required("CHORUS_TENANT_APPLICATIONS")) ?? [];
var readOnlyTenants = tenantApplications.ToDictionary(
    pair => pair.Key,
    pair => (IReadOnlyDictionary<string, string>)pair.Value.ToDictionary(
        app => app.Key, app => app.Value, StringComparer.Ordinal),
    StringComparer.Ordinal);

// Platform adapters: named platforms stay NotProductionReady stubs; the development
// simulator is registered only when explicitly enabled with its own signing key.
var adapters = new List<IPlatformLaunchAdapter> { new DouyinLaunchAdapter(), new KuaishouLaunchAdapter() };
var allowNotProductionReady = false;
if (Environment.GetEnvironmentVariable("CHORUS_PLATFORM_SIMULATOR") == "true")
{
    var simulatorKey = Convert.FromHexString(Required("CHORUS_SIMULATOR_SIGNING_KEY"));
    adapters.Add(new DevelopmentPlatformSimulator(simulatorKey));
    allowNotProductionReady = true;
}

var accessCredentialKey = Convert.FromHexString(Required("CHORUS_ACCESS_CREDENTIAL_KEY"));
Grpc.Net.Client.GrpcChannel DataChannel() => ProcessGrpcClientFactory.Create(
    new Uri(dataEndpoint), serverCertificate, root, nodeAuthority, clusterId, "data-server");

builder.Services.AddSingleton(trustedCertificates);
var actorRegistry = new StreamerActorRegistry();
builder.Services.AddSingleton(actorRegistry);
builder.Services.AddSingleton(new RedesignStreamerLoginCoordinator(
    new RedesignPlatformGateway(adapters, allowNotProductionReady),
    new StreamerIdentityChannel(DataChannel),
    new StreamerProfileChannel(DataChannel),
    actorRegistry,
    readOnlyTenants,
    accessCredentialKey));
// Instance self-registration (architecture 4.4): without a supervising node agent, the host
// tells the control plane itself where it serves, so routing can reach it.
var controlEndpoint = Environment.GetEnvironmentVariable("CHORUS_CONTROL_ENDPOINT");
if (controlEndpoint is { Length: > 0 })
{
    var identity = ProcessCertificateValidator.Validate(serverCertificate, root, clusterId, [nodeAuthority])
        ?? throw new InvalidOperationException("The StreamerServer certificate carries no process identity.");
    var reportEndpoint = Environment.GetEnvironmentVariable("CHORUS_REPORT_ENDPOINT")
        ?? $"https://localhost:{port}";
    var slotId = Environment.GetEnvironmentVariable("CHORUS_SLOT_ID") ?? $"{identity.NodeId}/{identity.Role}";
    var reportIntervalMs = int.Parse(Environment.GetEnvironmentVariable("CHORUS_REPORT_INTERVAL_MS") ?? "2000");
    builder.Services.AddHostedService(_ => new ProcessSelfReporter(
        serverCertificate, root, nodeAuthority, identity, controlEndpoint, reportEndpoint, slotId, reportIntervalMs));
}
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<StreamerSessionGrpcService>();
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before starting StreamerServer.");

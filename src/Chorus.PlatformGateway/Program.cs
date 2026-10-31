using System.Net;
using System.Security.Cryptography.X509Certificates;
using Chorus.Adapters.Abstractions;
using Chorus.Platform;
using Chorus.PlatformGateway;
using Chorus.Rpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var builder = WebApplication.CreateBuilder(args);
var clusterId = Required("CHORUS_CLUSTER_ID");
var root = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_ROOT_CA_PEM")));
var nodeAuthority = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_NODE_CA_PEM")));
var serverCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_TLS_CERT_PEM")),
    File.ReadAllText(Required("CHORUS_TLS_KEY_PEM")));
var trustedCertificates = new TrustedProcessCertificates();
var port = int.Parse(Environment.GetEnvironmentVariable("CHORUS_PORT") ?? "7504");
var dataPort = int.Parse(Environment.GetEnvironmentVariable("CHORUS_DATA_PORT") ?? "7501");
var dataEndpoint = Environment.GetEnvironmentVariable("CHORUS_DATA_ENDPOINT")
    ?? $"https://localhost:{dataPort}";
// The public webhook listener (architecture 6.1): platform callers present signatures, not
// mTLS client certificates, so the ingress face gets its own HTTP/1.1 listener. Absent by
// default — a gateway without ingress duty does not open a public port.
var ingressPortRaw = Environment.GetEnvironmentVariable("CHORUS_INGRESS_PORT");
if (ingressPortRaw is { Length: > 0 }
    && (!int.TryParse(ingressPortRaw, out var parsed) || parsed <= 0))
{
    throw new InvalidOperationException($"CHORUS_INGRESS_PORT must be a positive integer when set, not '{ingressPortRaw}'.");
}
var ingressPort = ingressPortRaw is { Length: > 0 } ? int.Parse(ingressPortRaw) : 0;

builder.WebHost.ConfigureKestrel(options =>
{
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
    if (ingressPort > 0)
    {
        options.Listen(IPAddress.Any, ingressPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseHttps(https => https.ServerCertificate = serverCertificate);
        });
    }
});

// No database credentials on this host (architecture 9.3): the lease protocol lives behind the
// data-plane channel, and this identity — the process certificate — is what the leases see.
var identity = ProcessCertificateValidator.Validate(serverCertificate, root, clusterId, [nodeAuthority])
    ?? throw new InvalidOperationException("The gateway certificate carries no process identity.");
Grpc.Net.Client.GrpcChannel DataChannel() => ProcessGrpcClientFactory.Create(
    new Uri(dataEndpoint), serverCertificate, root, nodeAuthority, clusterId, "data-server");

// Task adapters: the development simulator speaks plain HTTP to an endpoint the operator
// provides; adapters for real platforms stay NotProductionReady until their idempotency and
// query semantics are proven, so no real platform is wired here yet.
IPlatformTaskAdapter taskAdapter = new DevelopmentPlatformTaskAdapter(
    new Uri(Required("CHORUS_DEV_PLATFORM_ENDPOINT")));

builder.Services.AddSingleton(trustedCertificates);
builder.Services.AddHostedService(_ => new TaskReconciler(DataChannel, taskAdapter,
    identity.ProcessInstanceId,
    int.Parse(Environment.GetEnvironmentVariable("CHORUS_TASK_RECONCILE_INTERVAL_MS") ?? "1000"),
    int.Parse(Environment.GetEnvironmentVariable("CHORUS_TASK_LEASE_SECONDS") ?? "30"),
    int.Parse(Environment.GetEnvironmentVariable("CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS") ?? "30"),
    int.Parse(Environment.GetEnvironmentVariable("CHORUS_TASK_CALL_TIMEOUT_SECONDS") ?? "10")));
// Instance self-registration (architecture 4.4): without a supervising node agent, the host
// tells the control plane itself where it serves, so routing can reach it.
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
// The routing watch (FIX-INPUT-02): the webhook face resolves each callback's live key to the
// room it belongs to, and pushes straight at the owning game host. Without the control plane
// there is no routing to resolve, so the face keeps the durable attribution leg instead.
GatewayRoutingMonitor? routingMonitor = null;
if (controlEndpoint is { Length: > 0 })
{
    routingMonitor = new GatewayRoutingMonitor(
        () => ProcessGrpcClientFactory.Create(new Uri(controlEndpoint), serverCertificate,
            root, nodeAuthority, clusterId, "control-server"));
    builder.Services.AddSingleton(routingMonitor);
    builder.Services.AddHostedService(_ => new RoutingMonitorHost(routingMonitor));
}
builder.Services.AddGrpc();

// Platform modules (2026-10-30 pluginization): deployments face different platforms, so the
// gateway mounts only what Ingress:Platforms lists — an enabled module with missing
// credentials refuses to start naming the key, and an unlisted platform contributes zero
// registrations and zero credential reads.
var enabledPlatforms = PlatformModuleAssembly.AddPlatformModules(builder.Services, builder.Configuration);
if (ingressPort > 0)
{
    Func<string, Grpc.Net.Client.GrpcChannel> gameChannelFactory = endpoint =>
        ProcessGrpcClientFactory.Create(new Uri(endpoint), serverCertificate,
            root, nodeAuthority, clusterId, "game-server");
    builder.Services.AddSingleton(sp => new PlatformIngressEndpoint(
        sp.GetService<IPlatformEventVerifier>(),
        sp.GetServices<IPlatformEventMapper>(),
        new PlatformIngest.PlatformIngestClient(DataChannel()),
        sp.GetService<GatewayRoutingMonitor>(), gameChannelFactory));
}

var app = builder.Build();
// Host-visible startup evidence goes to the console channel the test harness captures —
// the same channel as the metrics dump, not the logger pipeline.
if (enabledPlatforms.Count > 0)
{
    Console.WriteLine($"platform modules enabled: {string.Join(", ", enabledPlatforms.Select(module => module.Name))}");
}
if (builder.Configuration.GetValue(PlatformModuleAssembly.AllowAllConfigurationKey, false))
{
    Console.Error.WriteLine(
        $"{PlatformModuleAssembly.AllowAllConfigurationKey} is enabled; platform signatures are not required. Development only.");
}
app.MapGrpcService<GatewayGrpcService>();
if (ingressPort > 0)
{
    // The webhook face answers only on the ingress listener's port; the declaration itself is
    // inert on the cluster listener where no platform caller can reach it.
    app.MapPost("/ingress/{platform}", async (string platform, HttpContext context,
        PlatformIngressEndpoint endpoint) => await endpoint.HandleAsync(platform, context));
}
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before starting the gateway.");

/// <summary>Owns the routing watch's lifetime (the StreamerGateway host's same pattern).</summary>
internal sealed class RoutingMonitorHost(GatewayRoutingMonitor monitor)
    : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => monitor.RunAsync(stoppingToken);
}

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
    new Uri($"https://localhost:{dataPort}"), dataIdentity, root, nodeAuthority, clusterId, "data-server");

builder.Services.AddSingleton(trustedCertificates);
builder.Services.AddSingleton(new RoomRuntime(DataChannel()));
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<GameHostGrpcService>();
app.MapGrpcService<RoundSyncGrpcService>();
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before starting GameServer.");

using System.Net;
using System.Security.Cryptography.X509Certificates;
using Chorus.ControlServer;
using Chorus.Data.Redesign;
using Chorus.Redesign.Rpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = Required("CHORUS_DATABASE_CONNECTION_STRING");
var clusterId = Required("CHORUS_CLUSTER_ID");
var root = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_ROOT_CA_PEM")));
var nodeAuthority = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_NODE_CA_PEM")));
var certPemPath = Required("CHORUS_TLS_CERT_PEM");
var keyPemPath = Required("CHORUS_TLS_KEY_PEM");
var serverCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(certPemPath), File.ReadAllText(keyPemPath));
var trustedCertificates = new TrustedProcessCertificates();
var port = int.Parse(Environment.GetEnvironmentVariable("CHORUS_PORT") ?? "7500");
await using (var database = new NpgsqlConnection(connectionString))
{
    await database.OpenAsync();
    await RedesignSchemaMigrator.VerifyAsync(database);
}

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
builder.Services.AddSingleton(trustedCertificates);
builder.Services.AddSingleton(new RedesignControlStore(connectionString));
builder.Services.AddSingleton(new RedesignCoordinatorStore(connectionString));
// The match coordinator hosts inside the control plane (ADR-R02) once game endpoints are
// configured; without them this is a plain control plane, matching nothing.
var gameEndpoints = Environment.GetEnvironmentVariable("CHORUS_GAME_ENDPOINTS");
if (gameEndpoints is { Length: > 0 })
{
    var dataEndpoint = Environment.GetEnvironmentVariable("CHORUS_DATA_ENDPOINT")
        ?? $"https://localhost:{Environment.GetEnvironmentVariable("CHORUS_DATA_PORT") ?? "7501"}";
    var identity = ProcessCertificateValidator.Validate(serverCertificate, root, clusterId, [nodeAuthority])
        ?? throw new InvalidOperationException("The ControlServer certificate carries no process identity.");
    var intervalMs = int.Parse(Environment.GetEnvironmentVariable("CHORUS_MATCH_INTERVAL_MS") ?? "500");
    builder.Services.AddSingleton(new ClusterDirectory(serverCertificate, root, nodeAuthority,
        clusterId, dataEndpoint,
        gameEndpoints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    builder.Services.AddSingleton(
        new MatchCoordinatorOptions(identity.ProcessInstanceId, intervalMs));
    builder.Services.AddHostedService<MatchCoordinatorService>();
}
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<ControlGrpcService>();
app.MapGrpcService<CoordinatorGrpcService>();
app.MapGrpcService<RoutingGrpcService>();
app.MapGrpcService<DeploymentGrpcService>();
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before starting ControlServer.");

public partial class Program;

using System.Net;
using System.Security.Cryptography.X509Certificates;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.Rpc;
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
builder.Services.AddSingleton(new RedesignAdminStore(connectionString));
// The channel directory is always built: the control plane reaches the data plane (takeover,
// recovery) and the configured game hosts whether or not matching is on.
var dataEndpoint = Environment.GetEnvironmentVariable("CHORUS_DATA_ENDPOINT")
    ?? $"https://localhost:{Environment.GetEnvironmentVariable("CHORUS_DATA_PORT") ?? "7501"}";
var gameEndpoints = Environment.GetEnvironmentVariable("CHORUS_GAME_ENDPOINTS");
// Configured endpoints are the bootstrap seed of the game-host directory (FIX-SCHED-01);
// the refresh loop then maintains it from valid process reports — new and restarted
// instances join without a restart here, and silent ones age out so their addresses are
// never used for a new assignment.
var directoryStaleSeconds = OptionalPositiveInt("CHORUS_DIRECTORY_STALE_SECONDS") ?? 60;
var directoryRefreshMs = OptionalPositiveInt("CHORUS_DIRECTORY_REFRESH_MS") ?? 2000;
builder.Services.AddSingleton(new ClusterDirectory(serverCertificate, root, nodeAuthority,
    clusterId, dataEndpoint,
    gameEndpoints is { Length: > 0 }
        ? gameEndpoints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [],
    TimeSpan.FromSeconds(directoryStaleSeconds)));
// The ownership lease registrar (architecture 4.2): renews live holders, lets silent ones expire.
builder.Services.AddHostedService<OwnershipRenewalService>();
// The recovery-deadline sweep (FIX-TAKE-05): closes expired open recoveries — reassigning
// to a different host under a newer epoch, or parking them for an operator.
builder.Services.AddHostedService<RoomRecoverySweepService>();
// The stall governor (FIX-TAKE-07): acts on rooms whose head froze while their host's
// heartbeat stayed fresh — migrating silent stalls, parking deterministic module faults.
builder.Services.AddHostedService<RoomStallGovernanceService>();
// The directory refresh loop (FIX-SCHED-01): the control plane's view of the game hosts
// follows the process reports, not the startup configuration alone.
builder.Services.AddHostedService(serviceProvider => new ClusterDirectoryRefreshService(
    serviceProvider.GetRequiredService<ClusterDirectory>(),
    serviceProvider.GetRequiredService<RedesignControlStore>(),
    TimeSpan.FromMilliseconds(directoryRefreshMs), TimeSpan.FromSeconds(directoryStaleSeconds)));
// The match coordinator hosts inside the control plane (ADR-R02). It pairs only while the
// directory holds live game hosts (FIX-SCHED-01) — seeded at startup or discovered since —
// so a control plane that starts before its game hosts still pairs once they report.
{
    var identity = ProcessCertificateValidator.Validate(serverCertificate, root, clusterId, [nodeAuthority])
        ?? throw new InvalidOperationException("The ControlServer certificate carries no process identity.");
    var intervalMs = int.Parse(Environment.GetEnvironmentVariable("CHORUS_MATCH_INTERVAL_MS") ?? "500");
    // The round length the coordinator asks for: the default keeps the demo cadence, a longer
    // window is how a test lets a client act mid-round instead of racing its settlement.
    var durationTicks = int.Parse(Environment.GetEnvironmentVariable("CHORUS_ROUND_DURATION_TICKS") ?? "40");
    builder.Services.AddSingleton(
        new MatchCoordinatorOptions(identity.ProcessInstanceId, intervalMs, durationTicks));
    builder.Services.AddHostedService<MatchCoordinatorService>();
}
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<ControlGrpcService>();
app.MapGrpcService<CoordinatorGrpcService>();
app.MapGrpcService<RoutingGrpcService>();
app.MapGrpcService<DeploymentGrpcService>();
app.MapGrpcService<TakeoverGrpcService>();
app.MapGrpcService<AdminGrpcService>();
app.MapGrpcService<RoundEndGrpcService>();
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before starting ControlServer.");

static int? OptionalPositiveInt(string name)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (raw is null) return null;
    if (!int.TryParse(raw, out var value) || value <= 0)
        throw new InvalidOperationException(
            $"{name} must be a positive integer when set, not '{raw}'.");
    return value;
}

public partial class Program;

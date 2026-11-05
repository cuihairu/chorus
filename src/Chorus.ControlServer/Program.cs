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
builder.Services.AddSingleton(new RedesignOperatorStore(connectionString));
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
// The end-operation discovery net (FIX-END-04): a room whose result froze without its
// operation — the host's report lost or the host dead — is begun from the room's receipt.
builder.Services.AddHostedService<RoomEndDiscoveryService>();
// Step 3's delivery leg (FIX-END-05): the coordinator persists the stop-delivery command with
// its responder set and pushes it at every authorized gateway — offline gateways are carried
// as pending responders, never skipped.
builder.Services.AddSingleton(new StopDeliveryNotifier(serverCertificate, root, nodeAuthority,
    clusterId));
builder.Services.AddHostedService<EndOperationCoordinatorService>();
// The participation-commit dispatcher (FIX-LINK-04): each round.started outbox event is
// delivered to the StreamerServer hosting every participant — the notice the mount binds its
// new room from, with the durable outbox row as the record and re-push as the reliability leg.
builder.Services.AddHostedService(serviceProvider => new ParticipationCommitDispatcherService(
    serviceProvider.GetRequiredService<RedesignControlStore>(),
    serverCertificate, root, nodeAuthority, clusterId,
    serviceProvider.GetRequiredService<ILogger<ParticipationCommitDispatcherService>>()));
// FIX-SYNC-08: the pause/resume driver — lifts a paused round once its ready threshold is
// met, and terminates one that exceeded its MaxPause by the module's freeze rule. The timeout
// leg runs first so the deadline is honored strictly.
builder.Services.AddHostedService<PauseResumeCoordinatorService>();
// The settlement business executor (architecture 9.3, FIX-END-11): completes committed
// manifests' rewards when the data host cannot — the queue and the grants live on the data
// plane; this loop only discovers and asks. FIX-SETTLE-03 adds the claim lease and the
// bounded retry: this instance id owns each room it works, a lease lapse makes a killed
// holder's room re-claimable, and the attempt budget escalates a manifest that keeps
// failing to queryable NeedsAttention instead of retrying forever.
var processIdentity = ProcessCertificateValidator.Validate(serverCertificate, root, clusterId, [nodeAuthority])
    ?? throw new InvalidOperationException("The ControlServer certificate carries no process identity.");
builder.Services.AddSingleton(new SettlementExecutorOptions(
    processIdentity.ProcessInstanceId,
    TimeSpan.FromSeconds(OptionalPositiveInt("CHORUS_SETTLE_LEASE_SECONDS") ?? 60),
    OptionalPositiveInt("CHORUS_SETTLE_MAX_ATTEMPTS") ?? 5));
builder.Services.AddHostedService<SettlementExecutorService>();
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
    var intervalMs = int.Parse(Environment.GetEnvironmentVariable("CHORUS_MATCH_INTERVAL_MS") ?? "500");
    // The round length the coordinator asks for: the default keeps the demo cadence, a longer
    // window is how a test lets a client act mid-round instead of racing its settlement.
    var durationTicks = int.Parse(Environment.GetEnvironmentVariable("CHORUS_ROUND_DURATION_TICKS") ?? "40");
    // FIX-END-16: how long the coordinator waits for a gateway to confirm the room's route
    // before it refuses to activate the battle (the room stays Preparing at Tick 0).
    var routeConfirmTimeoutMs = OptionalPositiveInt("CHORUS_ROUTE_CONFIRM_TIMEOUT_MS") ?? 10000;
    builder.Services.AddSingleton(
        new MatchCoordinatorOptions(processIdentity.ProcessInstanceId, intervalMs, durationTicks,
            routeConfirmTimeoutMs));
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
// The business path's match-intent entry (FIX-LINK-03): route-source gate, then the queue.
app.MapGrpcService<MatchIntentGrpcService>();
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

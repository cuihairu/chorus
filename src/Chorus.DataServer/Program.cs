using System.Net;
using System.Security.Cryptography.X509Certificates;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
MetricsConsoleDump.StartFromEnvironment();
var connectionString = Required("CHORUS_DATABASE_CONNECTION_STRING");
var clusterId = Required("CHORUS_CLUSTER_ID");
var root = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_ROOT_CA_PEM")));
var nodeAuthority = X509Certificate2.CreateFromPem(File.ReadAllText(Required("CHORUS_NODE_CA_PEM")));
var certPemPath = Required("CHORUS_TLS_CERT_PEM");
var keyPemPath = Required("CHORUS_TLS_KEY_PEM");
var serverCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(certPemPath), File.ReadAllText(keyPemPath));
var trustedCertificates = new TrustedProcessCertificates();
var port = int.Parse(Environment.GetEnvironmentVariable("CHORUS_PORT") ?? "7501");
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
builder.Services.AddSingleton(new RedesignPlayerStore(connectionString));
builder.Services.AddSingleton(new RedesignIdentityStore(connectionString));
builder.Services.AddSingleton(new RedesignBindingStore(connectionString));
builder.Services.AddSingleton(new RedesignInputEventStore(connectionString));
builder.Services.AddSingleton(new RedesignPlatformTaskStore(connectionString));
// Rooms and match reservations share the same admission cap: a reserved match holds its
// slot from reserve until commit or retirement.
var maxActive = int.TryParse(
    Environment.GetEnvironmentVariable("CHORUS_MAX_ACTIVE_ROOMS"), out var cap) && cap > 0
    ? cap
    : (int?)null;
builder.Services.AddSingleton(new RedesignMatchTicketStore(connectionString));
builder.Services.AddSingleton(new RedesignMatchReservationStore(connectionString,
    maxActiveReservations: maxActive));
builder.Services.AddSingleton(new RedesignRoomStore(connectionString, maxActiveRooms: maxActive));
builder.Services.AddSingleton(new RedesignRoundInputStore(connectionString));
builder.Services.AddSingleton(new RedesignFrameStore(connectionString));
builder.Services.AddSingleton(new RedesignRecoveryStore(connectionString));
// The settler's death, once (FIX-END-11): CHORUS_SETTLE_CRASH_AFTER_GRANT names a grant
// ordinal whose FIRST application in this process rolls back after its writes — the
// manifest keeps the room listed and the business executor completes it later. The seam
// fires once per process so the executor's own pass is never shot down.
var settleCrashOrdinal = 0;
if (int.TryParse(Environment.GetEnvironmentVariable("CHORUS_SETTLE_CRASH_AFTER_GRANT"),
        out var settleCrashAt) && settleCrashAt > 0)
    settleCrashOrdinal = settleCrashAt;
var settleCrashArmed = settleCrashOrdinal > 0;
builder.Services.AddSingleton(new RedesignSettlementStore(connectionString,
    settleCrashOrdinal > 0
        ? ordinal =>
        {
            if (ordinal >= settleCrashOrdinal && settleCrashArmed)
            {
                settleCrashArmed = false;
                throw new InvalidOperationException(
                    $"The settler died after grant {ordinal} (injected).");
            }
        }
        : null));
builder.Services.AddSingleton(new RedesignRetentionStore(connectionString));
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<DataGrpcService>();
app.MapGrpcService<StreamerIdentityGrpcService>();
app.MapGrpcService<PlatformIngestGrpcService>();
app.MapGrpcService<PlatformTaskGrpcService>();
app.MapGrpcService<MatchQueueGrpcService>();
app.MapGrpcService<MatchGrpcService>();
app.MapGrpcService<RoundGrpcService>();
app.MapGrpcService<RoundInputGrpcService>();
app.MapGrpcService<FrameGrpcService>();
app.MapGrpcService<SettlementGrpcService>();
app.MapGrpcService<RetentionGrpcService>();
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before starting DataServer.");

public partial class Program;

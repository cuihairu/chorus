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
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<ControlGrpcService>();
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} before starting ControlServer.");

public partial class Program;

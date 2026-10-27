using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Chorus.Data.Redesign;
using Chorus.Redesign.Rpc;
using Npgsql;

namespace Chorus.RoundDemo;

/// <summary>
/// The cluster contract the demo journey runs against: four real hosts, per-caller mTLS
/// certificates and a dedicated database. The standalone demo supplies its own process-spawned
/// cluster; the test suite supplies the same contract over its host environment, so both run
/// the identical scenario code.
/// </summary>
public interface IDemoCluster : IAsyncDisposable
{
    int ControlPort { get; }
    int DataPort { get; }
    int StreamerPort { get; }
    int GamePort { get; }
    string DatabaseConnectionString { get; }

    /// <summary>Starts control, data, streamer and game hosts; the streamer runs the
    /// development platform simulator under the given keys (architecture 5.3).</summary>
    Task StartAllAsync(byte[] simulatorSigningKey, byte[] accessCredentialKey);

    X509Certificate2 Certificate(string tenant, string role, string instance);

    Grpc.Net.Client.GrpcChannel Channel(int port, X509Certificate2 certificate, string role);
}

/// <summary>
/// A self-contained development cluster for the round demo: an in-process development root
/// and node authority, a dedicated migrated database, and the four real hosts started as
/// local processes with the environment contract every deployment uses. No database
/// credentials reach the game host (architecture 9.3) — it talks to the data plane only.
/// </summary>
public sealed class DemoCluster : IDemoCluster
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "chorus-round-demo-" + Guid.NewGuid().ToString("N"));
    // Inside the chorus_m2_% residue family, so the shared cleanup whitelist covers it.
    private readonly string _databaseName = "chorus_m2_demo_" + Guid.NewGuid().ToString("N");
    private readonly List<Process> _processes = [];
    private readonly Dictionary<Process, System.Text.StringBuilder> _hostOutput = [];
    private NpgsqlConnectionStringBuilder _admin = null!;
    private X509Certificate2 _root = null!;
    private X509Certificate2 _nodeAuthority = null!;
    private string _connectionString = "";

    public int ControlPort { get; } = FreePort();
    public int DataPort { get; } = FreePort();
    public int StreamerPort { get; } = FreePort();
    public int GamePort { get; } = FreePort();
    public string DatabaseConnectionString => _connectionString;

    public async Task StartAllAsync(byte[] simulatorSigningKey, byte[] accessCredentialKey)
    {
        var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
            ?? throw new InvalidOperationException(
                "Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
        _admin = new NpgsqlConnectionStringBuilder(source) { Pooling = false };
        await using (var admin = new NpgsqlConnection(_admin.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        _connectionString =
            new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _databaseName }
            .ConnectionString;
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        Directory.CreateDirectory(_directory);
        _root = ProcessCertificateIssuer.CreateRoot("cluster-a", DateTimeOffset.UtcNow.AddDays(10));
        _nodeAuthority = ProcessCertificateIssuer.CreateNodeAuthority(_root,
            new("cluster-a", "node-a",
                ["node-agent", "control-server", "data-server", "streamer-server", "game-server",
                    "platform-gateway"],
                ["@cluster", "tenant-a"]), DateTimeOffset.UtcNow.AddDays(5));
        File.WriteAllText(Path.Combine(_directory, "root.pem"), _root.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_directory, "node.pem"), _nodeAuthority.ExportCertificatePem());

        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorSigningKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessCredentialKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await StartAsync("Chorus.ControlServer", ControlPort, "control-server");
        await StartAsync("Chorus.DataServer", DataPort, "data-server");
        await StartAsync("Chorus.StreamerServer", StreamerPort, "streamer-server", streamerEnvironment);
        await StartAsync("Chorus.GameServer", GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");
    }

    public X509Certificate2 Certificate(string tenant, string role, string instance) =>
        ProcessCertificateIssuer.CreateProcess(_nodeAuthority,
            new("cluster-a", tenant, "node-a", role, instance), DateTimeOffset.UtcNow.AddDays(1),
            "localhost");

    public Grpc.Net.Client.GrpcChannel Channel(int port, X509Certificate2 certificate, string role) =>
        ProcessGrpcClientFactory.Create(new Uri($"https://localhost:{port}"), certificate,
            _root, _nodeAuthority, "cluster-a", role);

    private async Task StartAsync(string project, int port, string role,
        IReadOnlyDictionary<string, string>? extraEnvironment = null, string? clientTenant = null)
    {
        using var certificate = Certificate("@cluster", role, project);
        var certPath = Path.Combine(_directory, project + ".pem");
        var keyPath = Path.Combine(_directory, project + ".key");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        using (var key = certificate.GetECDsaPrivateKey())
            File.WriteAllText(keyPath, key!.ExportPkcs8PrivateKeyPem());
        var root = FindRoot();
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        var assembly = Path.Combine(root, "src", project, "bin", configuration, "net10.0", project + ".dll");
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(assembly);
        start.Environment["CHORUS_DATABASE_CONNECTION_STRING"] = _connectionString;
        start.Environment["CHORUS_CLUSTER_ID"] = "cluster-a";
        start.Environment["CHORUS_ROOT_CA_PEM"] = Path.Combine(_directory, "root.pem");
        start.Environment["CHORUS_NODE_CA_PEM"] = Path.Combine(_directory, "node.pem");
        start.Environment["CHORUS_TLS_CERT_PEM"] = certPath;
        start.Environment["CHORUS_TLS_KEY_PEM"] = keyPath;
        start.Environment["CHORUS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (name, value) in extraEnvironment ?? new Dictionary<string, string>())
            start.Environment[name] = value;
        if (clientTenant is { Length: > 0 })
        {
            // The game host serves under a cluster certificate but speaks for a tenant on
            // the data plane: it gets a second, tenant-scoped client identity.
            using var clientCertificate = Certificate(clientTenant, role, project + "-client");
            File.WriteAllText(Path.Combine(_directory, project + "-client.pem"),
                clientCertificate.ExportCertificatePem());
            using (var clientKey = clientCertificate.GetECDsaPrivateKey())
                File.WriteAllText(Path.Combine(_directory, project + "-client.key"),
                    clientKey!.ExportPkcs8PrivateKeyPem());
            start.Environment["CHORUS_CLIENT_CERT_PEM"] = Path.Combine(_directory, project + "-client.pem");
            start.Environment["CHORUS_CLIENT_KEY_PEM"] = Path.Combine(_directory, project + "-client.key");
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + project);
        var output = new System.Text.StringBuilder();
        _hostOutput.Add(process, output);
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add(process);
        // The streamer host's Ready only answers streamer-server callers; the rest accept
        // the node agent.
        using var caller = Certificate("@cluster",
            role == "streamer-server" ? "streamer-server" : "node-agent", "ready-probe");
        using var channel = Channel(port, caller, role);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"{project} exited with {process.ExitCode}.\n{output}");
            try
            {
                if (role == "control-server")
                    await new ControlService.ControlServiceClient(channel).ReadyAsync(new Empty(),
                        deadline: DateTime.UtcNow.AddSeconds(1));
                else if (role == "streamer-server")
                    await new StreamerSession.StreamerSessionClient(channel).ReadyAsync(new Empty(),
                        deadline: DateTime.UtcNow.AddSeconds(1));
                else if (role == "game-server")
                    await new GameHost.GameHostClient(channel).ReadyAsync(new Empty(),
                        deadline: DateTime.UtcNow.AddSeconds(1));
                else
                    await new DataService.DataServiceClient(channel).ReadyAsync(new Empty(),
                        deadline: DateTime.UtcNow.AddSeconds(1));
                return;
            }
            catch (Grpc.Core.RpcException) { await Task.Delay(100); }
        }
        throw new TimeoutException(project + " did not become ready.\n" + output);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
            process.Dispose();
        }
        _processes.Clear();
        if (_admin is not null)
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
        _nodeAuthority?.Dispose();
        _root?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chorus.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

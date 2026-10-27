using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.ControlServer;
using Chorus.Data.Redesign;
using Chorus.DataServer;
using Chorus.Protocol.Redesign;
using Chorus.Redesign.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignM0HostTests
{
    [Fact]
    public async Task EmptyDatabase_RealGrpcHostsEnforceRoleTenantEpochAndDurableReceipt()
    {
        await using var environment = await HostEnvironment.CreateAsync();
        using var nodeAgent = environment.Certificate("@cluster", "node-agent", "agent-1");
        using var controlActor = environment.Certificate("@cluster", "control-server", "control-actor");
        using var streamer = environment.Certificate("tenant-a", "streamer-server", "streamer-1");
        using var otherTenant = environment.Certificate("tenant-b", "streamer-server", "streamer-2");
        using var game = environment.Certificate("tenant-a", "game-server", "game-1");
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        using var controlChannel = environment.Channel(environment.ControlPort, controlActor, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-a",
            OwnerInstanceId = "game-1", LeaseSeconds = 15
        });
        Assert.Equal(1, owner.Epoch);

        using var dataChannel = environment.Channel(environment.DataPort, streamer, "data-server");
        var data = new DataService.DataServiceClient(dataChannel);
        var asset = new ChangeAssetRequest
        {
            Key = NewKey("tenant-a", "game-a", "asset.change", "player-a", "grant-1",
                new { AssetId = "coins", Delta = 10L, Reason = "test" }),
            AssetId = "coins", Delta = 10, Reason = "test"
        };
        var first = await data.ChangeAssetAsync(asset);
        Assert.Equal(10, first.Balance);
        Assert.False(first.Duplicate);

        // The response can disappear after DB commit; querying the original key still recovers it.
        var receipt = await data.GetOperationAsync(asset.Key);
        Assert.True(receipt.Found);
        var retry = await data.ChangeAssetAsync(asset);
        Assert.True(retry.Duplicate);
        Assert.Equal(10, retry.Balance);

        using var wrongTenantChannel = environment.Channel(environment.DataPort, otherTenant, "data-server");
        var wrongTenantData = new DataService.DataServiceClient(wrongTenantChannel);
        var deniedTenant = await Assert.ThrowsAsync<RpcException>(() => wrongTenantData.GetAssetAsync(new AssetRequest
        {
            TenantId = "tenant-a", GameId = "game-a", PlayerId = "player-a", AssetId = "coins"
        }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, deniedTenant.StatusCode);

        using var gameChannel = environment.Channel(environment.DataPort, game, "data-server");
        var gameData = new DataService.DataServiceClient(gameChannel);
        var deniedRole = await Assert.ThrowsAsync<RpcException>(() => gameData.ChangeAssetAsync(asset).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, deniedRole.StatusCode);
        var deniedReceiptRole = await Assert.ThrowsAsync<RpcException>(() => gameData.GetOperationAsync(asset.Key).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, deniedReceiptRole.StatusCode);

        var probe = new FencedProbeRequest
        {
            Key = NewKey("tenant-a", "game-a", "fenced.probe", "room-a", "tick-1",
                new { OwnerKind = "room", EntityId = "room-a", OwnerInstanceId = "game-1", Epoch = 1L, EffectId = "effect-1" }),
            OwnerKind = "room", EntityId = "room-a", OwnerInstanceId = "game-1", Epoch = 1, EffectId = "effect-1"
        };
        await gameData.CommitFencedProbeAsync(probe);
        await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-a",
            OwnerInstanceId = "game-2", LeaseSeconds = 15
        });
        probe.Key = NewKey("tenant-a", "game-a", "fenced.probe", "room-a", "tick-2",
            new { OwnerKind = "room", EntityId = "room-a", OwnerInstanceId = "game-1", Epoch = 1L, EffectId = "effect-2" });
        probe.EffectId = "effect-2";
        var stale = await Assert.ThrowsAsync<RpcException>(() => gameData.CommitFencedProbeAsync(probe).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);

        using var nodeChannel = environment.Channel(environment.ControlPort, nodeAgent, "control-server");
        var nodeControl = new ControlService.ControlServiceClient(nodeChannel);
        await nodeControl.RegisterNodeAsync(new RegisterNodeRequest
        {
            NodeId = "node-a", AgentInstanceId = "agent-1", CapabilitiesJson = "{}"
        });
        var process = await nodeControl.ReportProcessAsync(new ReportProcessRequest
        {
            NodeId = "node-a", AgentInstanceId = "agent-1", SlotId = "node-a/data-server",
            Role = "data-server", ProcessInstanceId = "data-1", ReportSequence = 1,
            Lifecycle = "Running", Readiness = "Ready", Health = "Healthy", Admission = "Accepting",
            EntityRecovery = "RecoveryFailed", StartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        Assert.True(process.Accepted);
        Assert.Equal("Ready", process.Readiness);
        Assert.Equal("RecoveryFailed", process.EntityRecovery);
    }

    private static OperationKey NewKey(string tenant, string game, string kind, string target, string operation, object canonical)
    {
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(canonical));
        return new OperationKey { TenantId = tenant, GameId = game, OperationKind = kind,
            TargetId = target, OperationId = operation, CanonicalVersion = 1, RequestHashHex = fingerprint.Sha256Hex };
    }

    private sealed class HostEnvironment : IAsyncDisposable
    {
        private readonly string _databaseName = "chorus_m0_host_" + Guid.NewGuid().ToString("N");
        private readonly List<Process> _processes = [];
        private readonly Dictionary<Process, System.Text.StringBuilder> _hostOutput = [];
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "chorus-m0-host-" + Guid.NewGuid().ToString("N"));
        private NpgsqlConnectionStringBuilder _admin = null!;
        private X509Certificate2 _root = null!;
        private X509Certificate2 _nodeAuthority = null!;
        private string _connectionString = "";
        public int ControlPort { get; } = FreePort();
        public int DataPort { get; } = FreePort();

        public static async Task<HostEnvironment> CreateAsync()
        {
            var result = new HostEnvironment();
            await result.InitializeAsync();
            return result;
        }

        private async Task InitializeAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
            _admin = new NpgsqlConnectionStringBuilder(source) { Pooling = false };
            await using (var admin = new NpgsqlConnection(_admin.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
                await create.ExecuteNonQueryAsync();
            }
            var database = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _databaseName };
            _connectionString = database.ConnectionString;
            await using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                await RedesignSchemaMigrator.ApplyAsync(connection);
            }
            Directory.CreateDirectory(_directory);
            _root = ProcessCertificateIssuer.CreateRoot("cluster-a", DateTimeOffset.UtcNow.AddDays(10));
            _nodeAuthority = ProcessCertificateIssuer.CreateNodeAuthority(_root,
                new("cluster-a", "node-a", ["node-agent", "control-server", "data-server", "streamer-server", "game-server"],
                    ["@cluster", "tenant-a", "tenant-b"]), DateTimeOffset.UtcNow.AddDays(5));
            File.WriteAllText(Path.Combine(_directory, "root.pem"), _root.ExportCertificatePem());
            File.WriteAllText(Path.Combine(_directory, "node.pem"), _nodeAuthority.ExportCertificatePem());
        }

        public X509Certificate2 Certificate(string tenant, string role, string instance) =>
            ProcessCertificateIssuer.CreateProcess(_nodeAuthority,
                new("cluster-a", tenant, "node-a", role, instance), DateTimeOffset.UtcNow.AddDays(1), "localhost");

        public Grpc.Net.Client.GrpcChannel Channel(int port, X509Certificate2 certificate, string role) =>
            ProcessGrpcClientFactory.Create(new Uri($"https://localhost:{port}"), certificate,
                _root, _nodeAuthority, "cluster-a", role);

        public async Task StartAsync(string project, int port, string role)
        {
            using var certificate = Certificate("@cluster", role, project);
            var certPath = Path.Combine(_directory, project + ".pem");
            var keyPath = Path.Combine(_directory, project + ".key");
            File.WriteAllText(certPath, certificate.ExportCertificatePem());
            using (var key = certificate.GetECDsaPrivateKey())
                File.WriteAllText(keyPath, key!.ExportPkcs8PrivateKeyPem());
            using var certificateProbe = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            var root = FindRoot();
            var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
            var assembly = Path.Combine(root, "src", project, "bin", configuration, "net10.0", project + ".dll");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true,
                RedirectStandardOutput = true };
            start.ArgumentList.Add(assembly);
            start.Environment["CHORUS_DATABASE_CONNECTION_STRING"] = _connectionString;
            start.Environment["CHORUS_CLUSTER_ID"] = "cluster-a";
            start.Environment["CHORUS_ROOT_CA_PEM"] = Path.Combine(_directory, "root.pem");
            start.Environment["CHORUS_NODE_CA_PEM"] = Path.Combine(_directory, "node.pem");
            start.Environment["CHORUS_TLS_CERT_PEM"] = certPath;
            start.Environment["CHORUS_TLS_KEY_PEM"] = keyPath;
            start.Environment["CHORUS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + project);
            var output = new System.Text.StringBuilder();
            _hostOutput.Add(process, output);
            process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data); };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _processes.Add(process);
            using var caller = Certificate("@cluster", "node-agent", "ready-probe");
            using var channel = Channel(port, caller, role);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited) throw new InvalidOperationException($"{project} exited with {process.ExitCode}.\n{output}");
                try
                {
                    if (role == "control-server")
                        await new ControlService.ControlServiceClient(channel).ReadyAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(1));
                    else
                        await new DataService.DataServiceClient(channel).ReadyAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(1));
                    return;
                }
                catch (RpcException) { await Task.Delay(100); }
            }
            throw new TimeoutException(project + " did not become ready.\n" + output);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var process in _processes)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            _nodeAuthority.Dispose();
            _root.Dispose();
            Directory.Delete(_directory, recursive: true);
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
}

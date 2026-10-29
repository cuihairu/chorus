using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    public async Task SignedBootstrapManifest_StartsAndReclaimsSeedHostsAfterAgentCrash()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var environment = await HostEnvironment.CreateAsync();
        var first = await environment.StartBootstrapAgentAsync();
        var firstAgent = await environment.WaitForAgentReportsAsync(first, expectedPreviousAgent: null);
        Assert.Equal(2, firstAgent.ReadyProcesses);

        first.Kill(); // Simulate an abrupt Agent crash; the independently sessioned children survive.
        // The setsid seed hosts inherit the agent's redirected pipes, so WaitForExitAsync would
        // block on output EOF forever; only the synchronous bounded wait returns on exit alone.
        if (!first.WaitForExit(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("NodeAgent did not exit after kill.");
        var second = await environment.StartBootstrapAgentAsync();
        var secondAgent = await environment.WaitForAgentReportsAsync(second, firstAgent.AgentInstanceId);
        Assert.NotEqual(firstAgent.AgentInstanceId, secondAgent.AgentInstanceId);
        Assert.Equal(2, secondAgent.ReadyProcesses);
    }

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

    public sealed class HostEnvironment : IAsyncDisposable
    {
        private readonly string _databaseName = "chorus_m0_host_" + Guid.NewGuid().ToString("N");
        private readonly List<Process> _processes = [];
        private readonly Dictionary<Process, string> _projectByProcess = new();
        private readonly List<Process> _agents = [];
        private readonly Dictionary<Process, System.Text.StringBuilder> _hostOutput = [];
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "chorus-m0-host-" + Guid.NewGuid().ToString("N"));
        private NpgsqlConnectionStringBuilder _admin = null!;
        private X509Certificate2 _root = null!;
        private X509Certificate2 _nodeAuthority = null!;
        private string _connectionString = "";
        public int ControlPort { get; } = FreePort();
        public int DataPort { get; } = FreePort();
        public int StreamerPort { get; } = FreePort();
        public int GamePort { get; } = FreePort();
        public string DatabaseConnectionString => _connectionString;

        /// <summary>The test root CA in PEM form — public-edge test clients (WSS) trust the
        /// same root the host certificates chain to, without disabling validation.</summary>
        public string RootCertificatePem => File.ReadAllText(Path.Combine(_directory, "root.pem"));

        /// <summary>The node authority (issuing intermediate) in PEM form — the hosts send
        /// their leaf certificates alone, so a validating client needs the intermediate in
        /// its trust store to build the chain.</summary>
        public string NodeAuthorityCertificatePem => File.ReadAllText(Path.Combine(_directory, "node.pem"));

        /// <summary>The spawned host processes by instance id — benchmark CPU accounting (test infra).</summary>
        public IReadOnlyList<(Process Process, string Instance)> HostProcesses =>
            _projectByProcess.Select(entry => (entry.Key, entry.Value)).ToList();

        /// <summary>The captured stdout of one host process — evidence assertions (takeover).</summary>
        public string HostOutput(Process process) =>
            _hostOutput.TryGetValue(process, out var output) ? output.ToString() : "";

        /// <summary>Everything every started host printed — the corpus the leak-scan tests
        /// grep for credential material that must never reach a log line.</summary>
        public string AllHostOutput
        {
            get
            {
                lock (_hostOutput)
                    return string.Join("\\n", _hostOutput.Values.Select(builder =>
                    {
                        lock (builder) return builder.ToString();
                    }));
            }
        }

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
                new("cluster-a", "node-a", ["node-agent", "control-server", "data-server", "streamer-server", "game-server", "platform-gateway", "streamer-gateway"],
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

        public async Task StartAsync(string project, int port, string role,
            IReadOnlyDictionary<string, string>? extraEnvironment = null, string? clientTenant = null,
            string? instanceId = null)
        {
            // The instance id names this process: certificate identity, key files and the kill
            // registry. The default keeps the single-instance behavior of earlier rounds; two
            // instances of one project pass distinct ids — including endpoint-shaped ids, made
            // filename-safe below while staying exact as an identity.
            var instance = instanceId ?? project;
            var instanceFiles = string.Concat(instance.Select(ch =>
                char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-'));
            using var certificate = Certificate("@cluster", role, instance);
            var certPath = Path.Combine(_directory, instanceFiles + ".pem");
            var keyPath = Path.Combine(_directory, instanceFiles + ".key");
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
            // The machine's inotify instance budget (128) is shared with everything else this
            // user runs; a full gate spawns dozens of hosts at once and each would otherwise
            // take a FileSystemWatcher instance for its configuration. Polling costs nothing
            // here — the hosts' files never change mid-test — and keeps the budget for others.
            start.Environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "true";
            foreach (var (name, value) in extraEnvironment ?? new Dictionary<string, string>())
                start.Environment[name] = value;
            if (clientTenant is { Length: > 0 })
            {
                // Some hosts serve under a cluster certificate but speak for a tenant on the
                // data plane (game-server): they get a second, tenant-scoped client identity.
                using var clientCertificate = Certificate(clientTenant, role, instance + "-client");
                File.WriteAllText(Path.Combine(_directory, instanceFiles + "-client.pem"),
                    clientCertificate.ExportCertificatePem());
                using (var clientKey = clientCertificate.GetECDsaPrivateKey())
                    File.WriteAllText(Path.Combine(_directory, instanceFiles + "-client.key"),
                        clientKey!.ExportPkcs8PrivateKeyPem());
                start.Environment["CHORUS_CLIENT_CERT_PEM"] = Path.Combine(_directory, instanceFiles + "-client.pem");
                start.Environment["CHORUS_CLIENT_KEY_PEM"] = Path.Combine(_directory, instanceFiles + "-client.key");
            }
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + project);
            var output = new System.Text.StringBuilder();
            _hostOutput.Add(process, output);
            process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data); };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _processes.Add(process);
            _projectByProcess[process] = instance;
            using var caller = Certificate("@cluster",
                role == "streamer-server" ? "streamer-server" : "node-agent", "ready-probe");
            using var channel = Channel(port, caller, role);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited) throw new InvalidOperationException($"{project} exited with {process.ExitCode}.\n{output}");
                try
                {
                    if (role == "control-server")
                        await new ControlService.ControlServiceClient(channel).ReadyAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(1));
                    else if (role == "streamer-server")
                        await new StreamerSession.StreamerSessionClient(channel).ReadyAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(1));
                    else if (role == "game-server")
                        await new GameHost.GameHostClient(channel).ReadyAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(1));
                    else if (role == "platform-gateway")
                        await new PlatformGatewayFace.PlatformGatewayFaceClient(channel).ReadyAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(1));
                    else if (role == "streamer-gateway")
                        await new StreamerGatewayFace.StreamerGatewayFaceClient(channel).ReadyAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(1));
                    else
                        await new DataService.DataServiceClient(channel).ReadyAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(1));
                    return;
                }
                catch (RpcException) { await Task.Delay(100); }
            }
            throw new TimeoutException(project + " did not become ready.\n" + output);
        }

        /// <summary>
        /// Kills a started host abruptly (simulated crash) by instance id — the project name for
        /// default starts, the instance id for multi-instance starts. Bounded synchronous wait: the
        /// hosts are setsid children whose redirected pipes would keep an async exit wait hanging.
        /// The process stays registered so dispose skips it as already exited; it can be started again.
        /// </summary>
        public void Kill(string instance)
        {
            foreach (var process in _projectByProcess.Where(pair => pair.Value == instance).Select(pair => pair.Key).ToList())
            {
                if (process.HasExited) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }

        public async Task<Process> StartBootstrapAgentAsync(string? gameProject = null, int? gamePort = null,
            IReadOnlyDictionary<string, string>? toolEnvironment = null,
            IReadOnlyDictionary<string, string>? agentEnvironment = null, bool joinCluster = false)
        {
            var root = FindRoot();
            var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
            var tool = Path.Combine(root, "src", "Chorus.BootstrapTool", "bin", configuration, "net10.0", "Chorus.BootstrapTool.dll");
            var control = Path.Combine(root, "src", "Chorus.ControlServer", "bin", configuration, "net10.0", "Chorus.ControlServer.dll");
            var data = Path.Combine(root, "src", "Chorus.DataServer", "bin", configuration, "net10.0", "Chorus.DataServer.dll");
            var bootstrapDirectory = Path.Combine(_directory, "bootstrap");
            var rootPassword = "test-root-password-" + Guid.NewGuid().ToString("N");
            var nodePassword = "test-node-password-" + Guid.NewGuid().ToString("N");
            var prepare = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true };
            prepare.ArgumentList.Add(tool);
            // Join mode provisions the node under this environment's own trust domain, so the
            // agent-hosted servers accept environment-minted clients and vice versa.
            var nodeId = joinCluster ? "node-a" : "node-bootstrap";
            foreach (var argument in new[] { bootstrapDirectory, "cluster-a", nodeId, control, data })
                prepare.ArgumentList.Add(argument);
            prepare.Environment["CHORUS_ROOT_PFX_PASSWORD"] = rootPassword;
            prepare.Environment["CHORUS_NODE_AUTHORITY_PFX_PASSWORD"] = nodePassword;
            if (joinCluster)
            {
                Directory.CreateDirectory(bootstrapDirectory);
                var rootJoin = Path.Combine(bootstrapDirectory, "join-root.pfx");
                var nodeJoin = Path.Combine(bootstrapDirectory, "join-node.pfx");
                File.WriteAllBytes(rootJoin, _root.Export(X509ContentType.Pkcs12, rootPassword));
                File.WriteAllBytes(nodeJoin, _nodeAuthority.Export(X509ContentType.Pkcs12, nodePassword));
                prepare.Environment["CHORUS_ROOT_PFX_FILE"] = rootJoin;
                prepare.Environment["CHORUS_NODE_AUTHORITY_PFX_FILE"] = nodeJoin;
            }
            prepare.Environment["CHORUS_CONTROL_PORT"] = ControlPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            prepare.Environment["CHORUS_DATA_PORT"] = DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (gameProject is { Length: > 0 } && gamePort is { } port)
            {
                prepare.Environment["CHORUS_GAME_DLL"] = Path.Combine(root, "src", gameProject, "bin",
                    configuration, "net10.0", gameProject + ".dll");
                prepare.Environment["CHORUS_GAME_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            foreach (var (name, value) in toolEnvironment ?? new Dictionary<string, string>())
                prepare.Environment[name] = value;
            using (var toolProcess = Process.Start(prepare) ?? throw new InvalidOperationException("BootstrapTool did not start."))
            {
                var error = await toolProcess.StandardError.ReadToEndAsync();
                await toolProcess.WaitForExitAsync();
                if (toolProcess.ExitCode != 0) throw new InvalidOperationException("BootstrapTool failed: " + error);
            }

            var agentPath = Path.Combine(root, "src", "Chorus.NodeAgent", "bin", configuration, "net10.0", "Chorus.NodeAgent.dll");
            var state = Path.Combine(_directory, "agent-state");
            Directory.CreateDirectory(state);
            var agentStart = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            agentStart.ArgumentList.Add(agentPath);
            agentStart.Environment["CHORUS_DATABASE_CONNECTION_STRING"] = _connectionString;
            agentStart.Environment["CHORUS_BOOTSTRAP_MANIFEST"] = Path.Combine(bootstrapDirectory, "node", "bootstrap.json");
            agentStart.Environment["CHORUS_ROOT_CA_PEM"] = Path.Combine(bootstrapDirectory, "node", "root-ca.pem");
            agentStart.Environment["CHORUS_NODE_AUTHORITY_PFX_PASSWORD"] = nodePassword;
            agentStart.Environment["CHORUS_AGENT_STATE_DIR"] = state;
            agentStart.Environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "true";
            foreach (var (name, value) in agentEnvironment ?? new Dictionary<string, string>())
                agentStart.Environment[name] = value;
            var process = Process.Start(agentStart) ?? throw new InvalidOperationException("NodeAgent did not start.");
            var output = new System.Text.StringBuilder();
            _hostOutput.Add(process, output);
            process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data); };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _processes.Add(process);
            _projectByProcess[process] = "node-agent";
            _agents.Add(process);
            return process;
        }

        public async Task<(string AgentInstanceId, int ReadyProcesses)> WaitForAgentReportsAsync(
            Process agent, string? expectedPreviousAgent, int expectedReady = 2,
            string nodeId = "node-bootstrap")
        {
            var deadline = DateTime.UtcNow.AddSeconds(35);
            while (DateTime.UtcNow < deadline)
            {
                if (agent.HasExited)
                    throw new InvalidOperationException("NodeAgent exited: " + AgentOutput(agent));
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT node.agent_instance_id, count(*) FILTER (
                        WHERE process.lifecycle='Running' AND process.readiness='Ready')::int
                    FROM control.node AS node
                    LEFT JOIN control.process AS process
                      ON process.node_id=node.node_id AND process.agent_instance_id=node.agent_instance_id
                    WHERE node.node_id=$1
                    GROUP BY node.agent_instance_id
                    """, connection);
                command.Parameters.AddWithValue(nodeId);
                await using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var instance = reader.GetString(0);
                    var ready = reader.GetInt32(1);
                    if (ready == expectedReady && (expectedPreviousAgent is null || instance != expectedPreviousAgent))
                        return (instance, ready);
                }
                await Task.Delay(200);
            }
            throw new TimeoutException(
                $"NodeAgent did not report {expectedReady} ready seed processes. " + AgentOutput(agent));
        }

        /// <summary>The agent's local command/process journal — plan evidence without trusting
        /// the control plane's view of the node.</summary>
        public IReadOnlyList<(string SlotId, int Pid, string DesiredState, string CommandId)> AgentJournal()
        {
            var path = Path.Combine(_directory, "agent-state", "process-journal.json");
            if (!File.Exists(path)) return [];
            var entries = JsonSerializer.Deserialize<AgentJournalEntry[]>(File.ReadAllText(path)) ?? [];
            return entries.Select(entry => (entry.SlotId, entry.Pid,
                entry.DesiredState ?? "Running", entry.CommandId ?? "")).ToList();
        }

        private sealed record AgentJournalEntry(string SlotId, string ProcessInstanceId, int Pid,
            DateTime StartedAtUtc, string? DesiredState, string? CommandId);

        private string AgentOutput(Process agent) => _hostOutput.TryGetValue(agent, out var output) ? output.ToString() : "";

        public async ValueTask DisposeAsync()
        {
            foreach (var process in _processes)
            {
                if (!process.HasExited && _agents.Contains(process) && OperatingSystem.IsLinux())
                {
                    _ = kill(process.Id, 15);
                    var grace = DateTime.UtcNow.AddSeconds(8);
                    while (!process.HasExited && DateTime.UtcNow < grace) await Task.Delay(100);
                }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                // Synchronous bounded wait: seed hosts inherit the redirected pipes, so an
                // unbounded WaitForExitAsync could block on output EOF until they die.
                process.WaitForExit(TimeSpan.FromSeconds(10));
                if (Environment.GetEnvironmentVariable("CHORUS_TEST_DUMP_HOST_OUTPUT") is not null
                    && _hostOutput.TryGetValue(process, out var captured))
                    Console.Error.WriteLine(
                        $"--- {_projectByProcess[process]} output ---\n{captured}");
                process.Dispose();
            }
            await KillOrphanedSeedHostsAsync();
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            _nodeAuthority.Dispose();
            _root.Dispose();
            Directory.Delete(_directory, recursive: true);
        }

        /// <summary>A crashed agent leaves its setsid seed hosts alive; they hold the database and ports.</summary>
        private async Task KillOrphanedSeedHostsAsync()
        {
            if (!OperatingSystem.IsLinux()) return;
            var journalPath = Path.Combine(_directory, "agent-state", "process-journal.json");
            if (!File.Exists(journalPath)) return;
            JournalEntry[] entries;
            try { entries = JsonSerializer.Deserialize<JournalEntry[]>(File.ReadAllText(journalPath)) ?? []; }
            catch (JsonException) { return; }
            foreach (var entry in entries)
            {
                try
                {
                    using var host = Process.GetProcessById(entry.Pid);
                    if (host.HasExited ||
                        Math.Abs((host.StartTime.ToUniversalTime() - entry.StartedAtUtc).TotalSeconds) >= 1)
                        continue;
                    host.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            await Task.Delay(500);
        }

        private sealed record JournalEntry(string SlotId, string ProcessInstanceId, int Pid, DateTime StartedAtUtc);

        /// <summary>An unused loopback port for extra host instances beyond the four defaults.</summary>
        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);

        private static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chorus.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}

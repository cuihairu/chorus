using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Chorus.Control;
using Chorus.Messaging.Ipc;
using Chorus.PlatformGateway;
using Chorus.RoundDemo;
using Microsoft.Extensions.Configuration;

namespace Chorus.Engine.Tests;

/// <summary>
/// The online gauges and the event chain's counters over the real chains that produce
/// them (FIX-OBS-17). The demo journey's real round — real platform ingests, a real room
/// host consuming them and committing frames, the real outbox dispatch into settlement —
/// lands in the ingress and delivery faces, and the gateway's own webhook face accepts a
/// development push and its retry through the listener the platform callbacks use; a real
/// gateway control loop — the HTTP ControlClient every control-mode gateway process runs —
/// keeps its sessions online through a real control host until its heartbeat stops and
/// the wall clock retires them. The faces are the stores the control host wires; the
/// endpoint shells were judged at store level in FIX-OBS-09/10/11 and are not re-tested.
/// </summary>
public sealed class RedesignObs17OnlineChainAcceptanceTests
{
    [Fact]
    public async Task TheRealRoundAndTheGatewayPushLandInTheIngressAndDeliveryFaces()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await using var cluster = new RoundDemoScenarioTests.HostEnvironmentCluster(environment);
        // The journey is the shared artifact: the same code path the console demo runs,
        // against real hosts — two streamers, one round to its settled result.
        var evidence = await RoundDemoScenario.RunAsync(cluster, Console.Out);
        Assert.Equal("Settled", evidence.RoomStatus);

        // One real push through the gateway's webhook face, then the platform's retry of
        // the same body: the retry is a fresh HTTP operation the durable answer calls a
        // duplicate. Exactly two pushes — the journey's own gifts ride the ingest service,
        // not this listener — so the gateway's cumulative counters are exact.
        using var http = await IngressHttpClientAsync(environment);
        var body = """{"sessionId":"live-demo-a","eventId":"obs17-push-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":50}""";
        var accepted = await PushAsync(http, cluster.IngressPort, body);
        Assert.True(HttpStatusCode.OK == accepted.StatusCode,
            $"The simulator callback was not accepted: {accepted.StatusCode} " +
            await accepted.Content.ReadAsStringAsync());
        var retried = await PushAsync(http, cluster.IngressPort, body);
        Assert.True(HttpStatusCode.OK == retried.StatusCode,
            $"The retried callback was not accepted: {retried.StatusCode} " +
            await retried.Content.ReadAsStringAsync());

        var ingress = new IngressStatsStore(cluster.DatabaseConnectionString, ControlDatabaseProvider.PostgreSql);
        IngressStats stats = await WaitUntil(() =>
        {
            var answer = ingress.Stats();
            return answer is { Accepted: 1, Duplicates: 1, Unverified: 0, Unmapped: 0,
                TooLarge: 0, TimedOut: 0, RateLimited: 0 } ? answer : null;
        }, TimeSpan.FromSeconds(60),
            "the gateway's push counters did not land in the ingress face.\n" + environment.AllHostOutput);
        var gateway = Assert.Single(stats.Gateways,
            entry => entry.ProcessInstanceId == "Chorus.PlatformGateway");
        Assert.Equal(1, gateway.Counters[IngressStatsStore.AcceptedFamily]);
        Assert.Equal(1, gateway.Counters[IngressStatsStore.DuplicatesFamily]);
        // The durable raw inputs: the journey's two gifts and the webhook push — all from
        // the simulator platform.
        var simulator = Assert.Single(stats.InputEventsBySource, source => source.Source == "simulator");
        Assert.True(simulator.Count >= 3, $"simulator inputs: {simulator.Count}");

        var delivery = new DeliveryStatsStore(cluster.DatabaseConnectionString, ControlDatabaseProvider.PostgreSql);
        DeliveryStats chain = await WaitUntil(() =>
        {
            var answer = delivery.Stats();
            return answer.EffectsApplied >= 2 && answer.EffectsDone >= 1 ? answer : null;
        }, TimeSpan.FromSeconds(60),
            "the round's delivery chain did not land in the delivery face.\n" + environment.AllHostOutput);
        // EffectsFailed is the attempt ledger's derivation (FIX-OBS-11): the end ladder's
        // stop-delivery rows land Stopped after the round closes and their hops count as
        // failed attempts — the round's own grants all settled Done, which the wait above
        // proves; the ledger's honest total is not zero and is not asserted as one.
        Assert.Equal(0, chain.Dropped);
        Assert.Equal(0, chain.Merged);
        // The session face's own counters, live from the room host's reporter: two routed
        // gifts pulled by the inbox and the boost command handed to the room — three
        // ingested, three applied by committed frames.
        Assert.Equal(3, chain.Ingested);
        Assert.Equal(3, chain.Applied);
        Assert.True(chain.Delivered >= 2, $"delivered: {chain.Delivered}");
        Assert.Contains(chain.Reporters, entry => entry.ProcessInstanceId == "Chorus.GameServer");
    }

    [Fact]
    public async Task ARealGatewayControlLoopKeepsItsSessionsOnlineUntilItsHeartbeatStops()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var port = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlHost = await StartControlHostAsync(environment, port);
        try
        {
            // The writer side is the production class: the same ControlClient every
            // control-mode gateway constructs, speaking the same nodes/report and
            // connections/renew calls GatewayIpcService's loop speaks. The loop reports
            // the gateway and the cluster's worker — the topology Connect's room
            // assignment assumes (a connection always lands on a healthy worker).
            var nodeId = "gateway-obs17";
            var instanceId = "gateway-obs17-1";
            var workerId = "worker-obs17";
            using var client = new ControlClient(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Control:Url"] = $"http://127.0.0.1:{port}",
                    ["Control:Key"] = "obs17-control-key-0123456789abcdef",
                }).Build(), nodeId);
            using var workerClient = new ControlClient(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Control:Url"] = $"http://127.0.0.1:{port}",
                    ["Control:Key"] = "obs17-control-key-0123456789abcdef",
                }).Build(), workerId);
            // One streamer session and one player's two sessions — the face's totals are
            // distinct users, so three connections answer two online users.
            ConnectionRecord[] sessions =
            [
                new("room-obs-1", "client-obs-a", "live-obs-a", "streamer", nodeId, instanceId, "gen-1"),
                new("room-obs-2", "client-obs-b", "player-obs-1", "player", nodeId, instanceId, "gen-1"),
                new("room-obs-3", "client-obs-c", "player-obs-1", "player", nodeId, instanceId, "gen-1"),
            ];
            using var loop = new CancellationTokenSource();
            var announced = false;
            Exception? lastWriterError = null;
            var writer = Task.Run(async () =>
            {
                while (!loop.IsCancellationRequested)
                {
                    try
                    {
                        await client.Post<NodeReport>("nodes/report", new NodeReport(nodeId, instanceId,
                            Environment.MachineName, "gateway", new IpcNetworkEndpoint("127.0.0.1", 0), 0),
                            loop.Token);
                        await workerClient.Post<NodeReport>("nodes/report", new NodeReport(workerId,
                            "worker-obs17-1", Environment.MachineName, "worker",
                            new IpcNetworkEndpoint("127.0.0.1", 0), 4), loop.Token);
                        if (!announced)
                        {
                            foreach (var session in sessions)
                                await client.Send("connections/connect", session, loop.Token);
                            announced = true;
                        }
                        else
                        {
                            await client.Send("connections/renew",
                                new { gatewayId = nodeId, instanceId, connections = sessions }, loop.Token);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The next tick retries, exactly like the gateway's own loop; the
                        // last refusal rides the failure message for the reader.
                        lastWriterError = ex;
                    }
                    try { await Task.Delay(1000, loop.Token); } catch (OperationCanceledException) { }
                }
            });

            var online = new OnlineStatsStore(environment.DatabaseConnectionString,
                ControlDatabaseProvider.PostgreSql,
                new ControlDatabase(environment.DatabaseConnectionString, ControlDatabaseProvider.PostgreSql));
            OnlineStats fresh = await WaitUntil(() =>
            {
                var answer = online.Stats();
                return answer is { PlayersTotal: 1, StreamersTotal: 1 } ? answer : null;
            }, TimeSpan.FromSeconds(30),
            "the gateway's sessions did not come online through the control host.\n" +
            (lastWriterError is null ? "" : $"last writer refusal: {lastWriterError}\n") + controlHost.Output);
            // No game.room rows exist under the control host's database: the splits answer
            // empty while the totals keep answering — the same-store rule OBS-09 pinned.
            Assert.Equal(Array.Empty<TenantGameCount>(), fresh.PlayersByTenantGame);

            // The real incident: the gateway stops reporting. Nothing deletes the records —
            // the wall clock ages the gateway's heartbeat past its lease and the face
            // retires every session that rode it.
            await loop.CancelAsync();
            try { await writer; } catch (OperationCanceledException) { }
            OnlineStats gone = await WaitUntil(() =>
            {
                var answer = online.Stats();
                return answer is { PlayersTotal: 0, StreamersTotal: 0 } ? answer : null;
            }, TimeSpan.FromSeconds(45),
                "the stopped gateway's sessions did not leave the online face.\n" + controlHost.Output);
        }
        finally
        {
            if (!controlHost.Process.HasExited) controlHost.Process.Kill(entireProcessTree: true);
            controlHost.Process.WaitForExit(TimeSpan.FromSeconds(10));
            controlHost.Process.Dispose();
        }
    }

    /// <summary>The control host as a real process over the environment's database: the
    /// production binary, the production configuration surface (env vars), plain HTTP —
    /// no operator ever signs in, so a seed hash is all the boot validation asks for.</summary>
    private static async Task<(Process Process, string Output)> StartControlHostAsync(
        RedesignM0HostTests.HostEnvironment environment, int port)
    {
        var root = FindRoot();
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        var assembly = Path.Combine(root, "src", "Chorus.ControlHost", "bin", configuration,
            "net10.0", "Chorus.ControlHost.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(assembly);
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["Control__Key"] = "obs17-control-key-0123456789abcdef";
        start.Environment["Control__Provider"] = "PostgreSql";
        start.Environment["Control__ConnectionString"] = environment.DatabaseConnectionString;
        start.Environment["Admin__Users__0__Name"] = "ops";
        start.Environment["Admin__Users__0__PasswordHash"] = "obs17-seed-hash";
        start.Environment["Admin__Users__0__Role"] = "admin";
        // The machine's inotify instance budget is shared; polling costs nothing here.
        start.Environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "true";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the control host.");
        var output = new System.Text.StringBuilder();
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
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"the control host exited with {process.ExitCode}.\n{output}");
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/health");
                if (response.IsSuccessStatusCode) return (process, output.ToString());
            }
            catch (HttpRequestException) { /* not listening yet */ }
            catch (IOException) { /* not listening yet */ }
            await Task.Delay(250);
        }
        throw new TimeoutException("the control host did not become ready.\n" + output);
    }

    private static async Task<HttpResponseMessage> PushAsync(HttpClient http, int ingressPort, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://localhost:{ingressPort}/ingress/simulator")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(PlatformIngressEndpoint.AppIdHeader, "app-1");
        return await http.SendAsync(request);
    }

    private static async Task<HttpClient> IngressHttpClientAsync(
        RedesignM0HostTests.HostEnvironment environment)
    {
        // The certificates must outlive this method: the validation callback keeps using
        // them for every connection the client ever makes.
        var root = X509Certificate2.CreateFromPem(environment.RootCertificatePem);
        var nodeAuthority = X509Certificate2.CreateFromPem(environment.NodeAuthorityCertificatePem);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var candidate = new X509Certificate2(certificate);
                using var validator = new X509Chain();
                validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                validator.ChainPolicy.CustomTrustStore.Add(root);
                validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
                return validator.Build(candidate);
            },
        };
        return await Task.FromResult(new HttpClient(handler));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chorus.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static async Task<T> WaitUntil<T>(Func<T?> probe, TimeSpan budget, string failure) where T : class
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } answer) return answer;
            await Task.Delay(500);
        }
        throw new TimeoutException(failure);
    }
}

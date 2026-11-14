using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.Control;
using Chorus.SimulatorPlatform;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The BOTS-2 panel's moving parts: scenarios are plain records in the control plane's
/// generic records table (they survive a restart by opening the same file again), and
/// the fleet service answers an operator's mistakes at the request — a bad script, a
/// malformed signing key, missing trust material, a plain-HTTP endpoint — while a real
/// run's lifecycle states name what happened (a refused endpoint fails visibly with the
/// error recorded; a stop answers true while the run lives and false once it is over).
/// </summary>
public sealed class SimulatorBotPanelTests
{
    private const string TinyToml = """
        [script]
        name = "tiny"
        duration = 1

        [streamer]
        openId = "bots-open-t"
        liveKey = "bots-live-t"

        [[segment]]
        from = 0
        to = 0.5
        commentPerSecond = 2
        """;

    private static readonly string HexKey = new('a', 64);

    private static readonly (string Cert, string Key, string Root, string Node) Pem = WritePemMaterial();

    private static (string Cert, string Key, string Root, string Node) WritePemMaterial()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chorus_bots2_pem_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=bots-panel-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        var paths = (Path.Combine(dir, "client.pem"), Path.Combine(dir, "client.key"),
            Path.Combine(dir, "root.pem"), Path.Combine(dir, "node.pem"));
        File.WriteAllText(paths.Item1, cert.ExportCertificatePem());
        File.WriteAllText(paths.Item2, rsa.ExportPkcs8PrivateKeyPem());
        // The root and node authority files only have to load; the fleet facts never
        // complete a TLS handshake, so nobody validates against them here.
        File.WriteAllText(paths.Item3, cert.ExportCertificatePem());
        File.WriteAllText(paths.Item4, cert.ExportCertificatePem());
        return paths;
    }

    private static string NewSqliteDatabase() =>
        Path.Combine(Path.Combine(Path.GetTempPath(), "chorus_bots2_" + Guid.NewGuid().ToString("N")), "control.db");

    private static BotRunConfig Config(string endpoint) => new(
        endpoint, "http://127.0.0.1:9/", "app-1", "game-t", HexKey, "cluster-a",
        Pem.Cert, Pem.Key, Pem.Root, Pem.Node);

    private static async Task<BotRunRecord?> AwaitTerminal(BotFleetService service, string id, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var run = service.Run(id);
            if (BotFleetService.Terminal(run.State)) return run;
            await Task.Delay(100);
        }
        return null;
    }

    [Fact]
    public void ScenarioStoreRoundTripsAndSurvivesAReopen()
    {
        var path = NewSqliteDatabase();
        var store = new BotScenarioStore(new ControlDatabase(path));
        store.Save(new BotScenario("sc-1", "Demo", TinyToml, "gm", 123));

        // A second store over the same file — a restarted control plane — sees the row.
        var reopened = new BotScenarioStore(new ControlDatabase(path));
        var fetched = reopened.Get("sc-1");
        Assert.NotNull(fetched);
        Assert.Equal("Demo", fetched!.Name);
        Assert.Equal(TinyToml, fetched.Toml);

        reopened.Save(fetched with { Name = "Renamed", UpdatedBy = "gm-2", UpdatedAtMs = 456 });
        Assert.Equal("Renamed", store.Get("sc-1")!.Name);
        Assert.Single(store.List());

        Assert.False(store.Delete("sc-missing"));
        Assert.True(store.Delete("sc-1"));
        Assert.Null(store.Get("sc-1"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void FleetRejectsBadScriptsAtTheRequest()
    {
        var service = new BotFleetService();
        var thrown = Assert.Throws<ArgumentException>(
            () => service.Start("sc-1", "not a toml document [[(", Config("https://127.0.0.1:9")));
        Assert.Contains("toml", thrown.Message);
    }

    [Fact]
    public void FleetRejectsMalformedSigningKeysAtTheRequest()
    {
        var service = new BotFleetService();
        var notHex = Assert.Throws<ArgumentException>(
            () => service.Start("sc-1", TinyToml, Config("https://127.0.0.1:9") with { SimulatorSigningKeyHex = "zz" }));
        Assert.Contains("simulatorSigningKeyHex", notHex.Message);
        var tooShort = Assert.Throws<ArgumentException>(
            () => service.Start("sc-1", TinyToml, Config("https://127.0.0.1:9") with { SimulatorSigningKeyHex = "aabb" }));
        Assert.Contains("at least 32 bytes", tooShort.Message);
    }

    [Fact]
    public void FleetRejectsMissingTrustMaterialAndPlainEndpointsAtTheRequest()
    {
        var service = new BotFleetService();
        var missing = Assert.Throws<ArgumentException>(
            () => service.Start("sc-1", TinyToml, Config("https://127.0.0.1:9") with { RootCaPemPath = "/nonexistent/root.pem" }));
        Assert.Contains("rootCaPemPath", missing.Message);
        var plain = Assert.Throws<ArgumentException>(
            () => service.Start("sc-1", TinyToml, Config("http://127.0.0.1:9")));
        Assert.Contains("HTTPS", plain.Message);
    }

    [Fact]
    public async Task RefusedEndpointFailsVisiblyAndCarriesTheError()
    {
        var service = new BotFleetService();
        var started = service.Start("sc-1", TinyToml, Config("https://127.0.0.1:9"));
        Assert.Equal("Starting", started.State);

        var run = await AwaitTerminal(service, started.Id, TimeSpan.FromSeconds(20));
        Assert.NotNull(run);
        Assert.Equal("Failed", run!.State);
        Assert.False(string.IsNullOrEmpty(run.Error));

        // Stop after the terminal answers false — there is nothing left to stop.
        Assert.False(service.Stop(started.Id));
        Assert.Equal("Failed", service.Run(started.Id).State);
    }

    [Fact]
    public async Task StopAnswersTrueWhileTheRunLivesAndTheRunNamesItselfStopped()
    {
        // A listener that accepts the connection but never answers the handshake keeps
        // the mount waiting — the deterministic stand-in for a wedged streamer gateway.
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var service = new BotFleetService();
        var started = service.Start("sc-1", TinyToml, Config($"https://127.0.0.1:{port}"));

        Assert.True(service.Stop(started.Id));
        var stopped = await AwaitTerminal(service, started.Id, TimeSpan.FromSeconds(15));
        Assert.NotNull(stopped);
        // The error rides the assertion so a fast-failing dial names itself here.
        Assert.True(stopped!.State == "Stopped", $"{stopped.State}: {stopped.Error}");
        Assert.False(service.Stop(started.Id));
        listener.Stop();
    }

    [Fact]
    public void UnknownRunAnswersNotFound()
    {
        var service = new BotFleetService();
        Assert.Throws<KeyNotFoundException>(() => service.Stop("bots-run-404"));
        Assert.Throws<KeyNotFoundException>(() => service.Run("bots-run-404"));
    }
}

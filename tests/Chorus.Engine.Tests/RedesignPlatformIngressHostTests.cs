using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Chorus.Adapters.Platforms;
using Chorus.Redesign.Platform;
using Chorus.Redesign.PlatformGateway;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The redesign ingress face on real hosts (old-line deletion parity, 2026-10-30): each
/// platform's callback — signed the way that platform signs — enters through the gateway's
/// public listener, passes the module-assembled verifier and mapper, and lands as one
/// durable data-plane batch. Real platforms cannot be mounted (their launch adapters fail
/// closed pending production admission), so their events honestly wait in the persistent
/// inbox; the simulator leg — the one platform a development fleet can mount — runs the
/// whole chain from HTTP callback to a room delivery.
/// </summary>
public sealed class RedesignPlatformIngressHostTests
{
    private const string DouyinSecret = "parity-douyin-secret";
    private const string KuaishouToken = "parity-kuaishou-token";
    private const string BilibiliKey = "parity-bili-key";
    private const string BilibiliSession = "live-parity-bilibili";
    // 32 raw key bytes for the kuaishou AES envelope; the config carries them base64.
    private static readonly byte[] KuaishouKey = Convert.FromHexString(
        "4b75616973686f755061726974794b6579313233343536373839303132333435");

    [Fact]
    public async Task EachRealPlatformCallbackVerifiesMapsAndDurablyWaitsForAttribution()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.Redesign.PlatformGateway", gatewayPort, "platform-gateway",
            GatewayEnvironment(environment.DataPort, ingressPort, allowAll: false,
                "douyin,kuaishou,bilibili",
                ["Ingress:Douyin:AppSecret", DouyinSecret],
                ["Ingress:Kuaishou:VerifyToken", KuaishouToken],
                ["Ingress:Kuaishou:MsgEncryptKey", Convert.ToBase64String(KuaishouKey)],
                ["Ingress:Bilibili:AccessKeyId", BilibiliKey],
                ["Ingress:Bilibili:AccessKeySecret", "parity-bili-secret"],
                ["Ingress:Bilibili:SessionId", BilibiliSession]),
            instanceId: "gateway-parity-real");

        using var http = await HttpClientAsync(environment);

        // Douyin: HMAC-SHA256(app secret, body) in the platform's signature header.
        var douyinBody = """{"event":"gift","event_id":"parity-douyin-1","session_id":"live-parity-douyin","gift_id":"g1","gift_count":1,"gift_value":100}""";
        var douyin = await PostAsync(http, ingressPort, "douyin", "app-1", douyinBody,
            (DouyinPlatformEventVerifier.DefaultSignatureHeader, HmacHex(DouyinSecret, douyinBody)));
        AssertReceiptWaitsForAttribution(douyin);

        // Kuaishou: sha1Hex(body + verify token) in kwaisign over the AES-256-CBC envelope.
        var kuaishouBody = KuaishouEnvelope("parity-kuaishou-1", """{"event":"gift","session_id":"live-parity-kuaishou","gift_id":"g2","gift_count":2,"gift_value":50}""");
        var kuaishou = await PostAsync(http, ingressPort, "kuaishou", "app-1", kuaishouBody,
            ("kwaisign", Sha1Hex(kuaishouBody + KuaishouToken)));
        AssertReceiptWaitsForAttribution(kuaishou);

        // Bilibili: x-bili-* headers over the DANMU_MSG payload; the session is the
        // configured open-live binding, so the canonical key is the configured session.
        var bilibiliBody = """{"cmd":"DANMU_MSG","msg_id":"parity-bili-1","data":{"open_id":"ou-1","uname":"viewer","msg":"666"}}""";
        var bilibiliHeaders = BilibiliSigner.BuildHeaders(bilibiliBody, BilibiliKey,
            "parity-bili-secret", DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Guid.NewGuid().ToString("N"));
        var bilibili = await PostAsync(http, ingressPort, "bilibili", "app-1", bilibiliBody,
            bilibiliHeaders.Select(pair => (pair.Key, pair.Value)).ToArray());
        AssertReceiptWaitsForAttribution(bilibili);

        // The three platforms' events are durable: attribution is platform-strict and no
        // mount can exist on them (launch adapters fail closed), so the persistent inbox —
        // not a drop, not an in-memory ack — is where they wait.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT platform,disposition,durability FROM input.event " +
                "WHERE source_dedup_key IN ('parity-douyin-1','parity-kuaishou-1','parity-bili-1') " +
                "ORDER BY platform", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string Platform, string Disposition, string Durability)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal("PendingAttribution", row.Disposition);
                Assert.Equal("Durable", row.Durability);
            });
        }

        // The module verifiers are really in the chain: a tampered douyin signature is
        // refused, and the simulator platform — mounted only under AllowAll, which this
        // gateway did not enable — fails closed too.
        var tampered = await PostAsync(http, ingressPort, "douyin", "app-1", douyinBody,
            (DouyinPlatformEventVerifier.DefaultSignatureHeader, HmacHex("wrong-secret", douyinBody)));
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
        var simulator = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"live-parity-sim","kind":"Gift"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, simulator.StatusCode);
    }

    [Fact]
    public async Task TheSimulatorCallbackRunsTheWholeChainIntoARoomDelivery()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        // One real platform keeps the verifier router mounted; AllowAll adds the development
        // verifier plus the simulator mapper — the leg a development fleet can drive home.
        await environment.StartAsync("Chorus.Redesign.PlatformGateway", gatewayPort, "platform-gateway",
            GatewayEnvironment(environment.DataPort, ingressPort, allowAll: true,
                "douyin",
                ["Ingress:Douyin:AppSecret", DouyinSecret]),
            instanceId: "gateway-parity-sim");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (login, bindingId) = await MountAsync(environment, simulator, "open-parity", "live-parity-sim");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-parity");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Redesign.Rpc.ControlService.ControlServiceClient(controlChannel);
        var owner = await control.ClaimOwnershipAsync(new Chorus.Redesign.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-parity-1",
            OwnerInstanceId = "game-1", LeaseSeconds = 15
        });
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-parity");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Redesign.Rpc.RoundStore.RoundStoreClient(gameChannel);
        var roundRequest = new Chorus.Redesign.Rpc.StartRoundRequest
        {
            RoomId = "room-parity-1", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "parity-round-1"
        };
        roundRequest.Participants.Add(new Chorus.Redesign.Rpc.RoundParticipantProto
        {
            BindingId = bindingId, ParticipantId = login.StreamerId, Role = "Host"
        });
        await rounds.StartRoundAsync(roundRequest);

        using var http = await HttpClientAsync(environment);
        var body = """{"sessionId":"live-parity-sim","eventId":"parity-sim-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""";
        var accepted = await PostAsync(http, ingressPort, "simulator", "app-1", body);
        Assert.True(HttpStatusCode.OK == accepted.StatusCode,
            $"The simulator callback was not accepted: {accepted.StatusCode} " +
            await accepted.Content.ReadAsStringAsync());
        var receipt = Assert.Single(ReadReceipts(await accepted.Content.ReadAsStringAsync()));
        Assert.Equal("Attributed", receipt.GetProperty("disposition").GetString());
        Assert.Equal(bindingId, receipt.GetProperty("bindingId").GetString());

        // The platform's retry of the same callback is a fresh HTTP operation whose event id
        // the store answers as a duplicate with its original attribution.
        var retried = await PostAsync(http, ingressPort, "simulator", "app-1", body);
        Assert.True(HttpStatusCode.OK == retried.StatusCode,
            $"The retried callback was not accepted: {retried.StatusCode} " +
            await retried.Content.ReadAsStringAsync());
        var duplicate = Assert.Single(ReadReceipts(await retried.Content.ReadAsStringAsync()));
        Assert.True(duplicate.GetProperty("duplicate").GetBoolean());
        Assert.Equal(bindingId, duplicate.GetProperty("bindingId").GetString());

        // The room's driver pulls the attributed batch: one delivery into the round.
        var routed = await new Chorus.Redesign.Rpc.RoundInput.RoundInputClient(gameChannel)
            .RouteEventsAsync(new Chorus.Redesign.Rpc.RouteEventsRequest
            {
                Platform = "simulator", AppId = "app-1", OperationId = "parity-route-1", MaxEvents = 10
            });
        var delivery = Assert.Single(routed.Events);
        Assert.Equal("Routed", delivery.Disposition);
        Assert.Equal("room-parity-1", delivery.RoomId);

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery " +
                "WHERE consumer_scope='room:room-parity-1' AND state='Pending'", connection);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    private static void AssertReceiptWaitsForAttribution(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = Assert.Single(ReadReceipts(response.Content.ReadAsStringAsync()
            .GetAwaiter().GetResult()));
        Assert.Equal("PendingAttribution", receipt.GetProperty("disposition").GetString());
        Assert.Equal("", receipt.GetProperty("bindingId").GetString());
    }

    private static List<JsonElement> ReadReceipts(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("receipts").EnumerateArray()
            .Select(receipt => receipt.Clone()).ToList();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, int ingressPort,
        string platform, string appId, string body, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://localhost:{ingressPort}/ingress/{platform}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(PlatformIngressEndpoint.AppIdHeader, appId);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return await http.SendAsync(request);
    }

    private static async Task<HttpClient> HttpClientAsync(
        RedesignM0HostTests.HostEnvironment environment)
    {
        // The certificates must outlive this method: the validation callback keeps using
        // them for every connection the client ever makes.
        var root = X509Certificate2.CreateFromPem(environment.RootCertificatePem);
        var nodeAuthority = X509Certificate2.CreateFromPem(environment.NodeAuthorityCertificatePem);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, chain, _) =>
            {
                if (certificate is null) return false;
                using var candidate = new X509Certificate2(certificate);
                using var validator = new X509Chain();
                validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                validator.ChainPolicy.CustomTrustStore.Add(root);
                // The server presents its leaf certificate alone, so the issuing node authority
                // must be supplied to build the intermediate hop.
                validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
                return validator.Build(candidate);
            }
        };
        return await Task.FromResult(new HttpClient(handler));
    }

    private static Dictionary<string, string> GatewayEnvironment(int dataPort, int ingressPort,
        bool allowAll, string platforms, params string[][] settings)
    {
        var environment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = dataPort.ToString(CultureInfo.InvariantCulture),
            ["CHORUS_DEV_PLATFORM_ENDPOINT"] = "http://127.0.0.1:9/",
            ["CHORUS_INGRESS_PORT"] = ingressPort.ToString(CultureInfo.InvariantCulture),
            ["Ingress__Platforms__0"] = platforms.Split(',')[0]
        };
        var index = 1;
        foreach (var platform in platforms.Split(',').Skip(1))
            environment[$"Ingress__Platforms__{index++}"] = platform;
        if (allowAll)
            environment["Ingress__AllowAll"] = "true";
        foreach (var pair in settings)
            environment[pair[0].Replace(":", "__")] = pair[1];
        return environment;
    }

    private static string HmacHex(string secret, string body) =>
        Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

    private static string Sha1Hex(string input) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    /// <summary>The official kuaishou envelope: AES-256-CBC/PKCS7 over the inner event JSON,
    /// key = the configured 32 bytes, IV = the first 16 key bytes.</summary>
    private static string KuaishouEnvelope(string msgId, string innerJson)
    {
        using var aes = Aes.Create();
        aes.Key = KuaishouKey;
        aes.IV = KuaishouKey.AsSpan(0, 16).ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(innerJson), 0,
            Encoding.UTF8.GetBytes(innerJson).Length);
        return JsonSerializer.Serialize(new
        {
            encryptedMsg = Convert.ToBase64String(cipher),
            msgId,
            componentAppId = "app-1",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private static async Task<(Chorus.Redesign.Rpc.LoginReadyReply Login, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId, string liveKey)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server",
            "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new Chorus.Redesign.Rpc.StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new Chorus.Redesign.Rpc.LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new Chorus.Redesign.Rpc.StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
        return (login, mounted.BindingId);
    }
}

using System.Text.Json;
using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// Load-tool unit coverage: the Douyin payload shape the mapper accepts, the
/// duplicate-id behaviour, and CLI option parsing.
/// </summary>
public sealed class LoadToolTests
{
    [Fact]
    public void Payload_TestPlatformHasTestMapperFields()
    {
        var factory = new EventPayloadFactory("test", userPoolSize: 100, giftRate: 0, duplicateRate: 0, matchId: "m2");
        var payload = factory.Create("load-s1");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.Equal("load-s1", root.GetProperty("sessionId").GetString());
        Assert.True(root.TryGetProperty("eventId", out _));
        Assert.True(root.TryGetProperty("userId", out _));
        Assert.True(root.GetProperty("kind").GetString() is "comment" or "like");
        Assert.Equal("m2", root.GetProperty("matchId").GetString());
    }

    [Fact]
    public void Payload_EventNamespacePrefixesFreshIds()
    {
        var factory = new EventPayloadFactory(
            "test", userPoolSize: 100, giftRate: 0, duplicateRate: 0, matchId: null,
            sequenceProvider: () => 42, eventIdPrefix: "shard2-");

        var payload = factory.Create("load-s1");

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal("shard2-load-s1-e42", doc.RootElement.GetProperty("eventId").GetString());
    }

    [Fact]
    public void Payload_HasDouyinMapperFields()
    {
        var factory = new EventPayloadFactory("douyin", userPoolSize: 100, giftRate: 0, duplicateRate: 0, matchId: null);
        var payload = factory.Create("load-s1");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.Equal("load-s1", root.GetProperty("session_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("event_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("user_id").GetString()));
        Assert.True(root.GetProperty("event").GetString() is "comment" or "like");
    }

    [Fact]
    public void Payload_GiftEventsCarryGiftFields()
    {
        var factory = new EventPayloadFactory("douyin", userPoolSize: 100, giftRate: 1, duplicateRate: 0, matchId: "m1");
        var payload = factory.Create("load-s1");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.Equal("gift", root.GetProperty("event").GetString());
        Assert.True(root.TryGetProperty("gift_id", out _));
        Assert.True(root.TryGetProperty("gift_count", out _));
        Assert.Equal("m1", root.GetProperty("match_id").GetString());
    }

    [Fact]
    public void DuplicateRate_ReusesRecentEventIds()
    {
        var factory = new EventPayloadFactory("douyin", userPoolSize: 100, giftRate: 0, duplicateRate: 1, matchId: null);
        var first = JsonDocument.Parse(factory.Create("s")).RootElement.GetProperty("event_id").GetString();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 20; i++)
        {
            using var doc = JsonDocument.Parse(factory.Create("s"));
            seen.Add(doc.RootElement.GetProperty("event_id").GetString()!);
        }

        Assert.Contains(first!, seen);
        Assert.True(seen.Count < 20, "with dup-rate 1.0 every id should come from the recent window");
    }

    [Fact]
    public void Options_ParsesFlags()
    {
        var options = LoadToolOptions.Parse(new[]
        {
            "--url", "http://127.0.0.1:5168",
            "--sessions", "7",
            "--rate", "10",
            "--clients", "2",
            "--duration", "30",
            "--ack-mix", "slow",
            "--match", "m9",
        });

        Assert.Equal("http://127.0.0.1:5168", options.BaseUrl);
        Assert.Equal(7, options.Sessions);
        Assert.Equal(10, options.EventsPerSecondTotal);
        Assert.Equal(2, options.ClientsPerSession);
        Assert.Equal(30, options.DurationSeconds);
        Assert.Equal("slow", options.AckMix);
        Assert.Equal("m9", options.MatchId);
    }

    [Fact]
    public void Options_ParsesRemainingFlags()
    {
        var options = LoadToolOptions.Parse(new[]
        {
            "--platform", "bilibili",
            "--producers", "8",
            "--disconnect-every", "45",
            "--reconnect-ms", "250",
            "--users", "100",
            "--gift-rate", "0.5",
            "--dup-rate", "0.1",
            "--token", "t1",
            "--engine-metrics", "http://127.0.0.1:9/metrics",
            "--report-every", "2",
        });

        Assert.Equal("bilibili", options.Platform);
        Assert.Equal(8, options.ProducerConnections);
        Assert.Equal(45, options.DisconnectEverySeconds);
        Assert.Equal(250, options.ReconnectBackoffMs);
        Assert.Equal(100, options.UserPoolSize);
        Assert.Equal(0.5, options.GiftRate);
        Assert.Equal(0.1, options.DuplicateRate);
        Assert.Equal("t1", options.Token);
        Assert.Equal("http://127.0.0.1:9/metrics", options.EngineMetricsUrl);
        Assert.Equal(2, options.ReportEverySeconds);
    }

    [Fact]
    public void Options_ParsesWsUrlFlag()
    {
        var options = LoadToolOptions.Parse(new[]
        {
            "--url", "http://127.0.0.1:5168",
            "--ws-url", "ws://127.0.0.1:5169/ws",
        });

        Assert.Equal("ws://127.0.0.1:5169/ws", options.WsUrl);
        Assert.Equal("ws://127.0.0.1:5169/ws", options.EffectiveWsUrl);
    }

    [Fact]
    public void Options_ParseDriverShard()
    {
        var options = LoadToolOptions.Parse(new[]
        {
            "--rate", "30", "--driver-shard-index", "1", "--driver-shard-count", "3",
            "--driver-rate", "10.5", "--event-namespace", "shard2",
        });

        Assert.Equal(1, options.DriverShardIndex);
        Assert.Equal(3, options.DriverShardCount);
        Assert.Equal(10.5, options.EffectiveEventsPerSecondTotal);
        Assert.Equal("shard2-", options.EventIdPrefix);
        Assert.Equal("shard2-", options.ClientIdPrefix);
        Assert.Equal(" shard2", options.ShardLabel);
    }

    [Fact]
    public void Options_RejectsInvalidDriverShard()
    {
        Assert.Throws<ArgumentException>(() => LoadToolOptions.Parse(["--driver-processes", "0"]));
        Assert.Throws<ArgumentException>(() => LoadToolOptions.Parse(["--driver-shard-index", "1"]));
        Assert.Throws<ArgumentException>(() => LoadToolOptions.Parse(
        [
            "--driver-shard-index", "2", "--driver-shard-count", "2",
        ]));
        Assert.Throws<ArgumentException>(() => LoadToolOptions.Parse(
        [
            "--driver-shard-index", "0", "--driver-shard-count", "2", "--driver-processes", "2",
        ]));
    }

    [Fact]
    public void MultiProcessDriver_SplitsRateAndNamespace()
    {
        Assert.Equal(12, MultiProcessDriver.ShardRate(36, 0, 3));
        Assert.Equal(12, MultiProcessDriver.ShardRate(36, 1, 3));
        Assert.Equal(12, MultiProcessDriver.ShardRate(36, 2, 3));
        Assert.Equal("shard2", MultiProcessDriver.ShardNamespace(1));
    }

    [Fact]
    public void MultiProcessDriver_BuildsIsolatedShardArgs()
    {
        var args = MultiProcessDriver.BuildShardArgs(
        [
            "--url", "http://stub", "--driver-processes", "3", "--rate", "300",
            "--event-namespace", "user-value", "--ws-url", "ws://stub/ws",
        ], shardIndex: 1, shardCount: 3, shardRate: 100);

        Assert.Equal(14, args.Length);
        Assert.Equal("--driver-shard-index", args[^8]);
        Assert.Equal("1", args[^7]);
        Assert.Equal("--driver-shard-count", args[^6]);
        Assert.Equal("3", args[^5]);
        Assert.Equal("--driver-rate", args[^4]);
        Assert.Equal("100", args[^3]);
        Assert.Equal("--event-namespace", args[^2]);
        Assert.Equal("shard2", args[^1]);
        Assert.DoesNotContain(args, value => value == "3" && Array.IndexOf(args, value) == Array.IndexOf(args, "--driver-processes") + 1);
    }

    [Fact]
    public void MultiProcessDriver_DotnetDllExecutableRequiresAssemblyArgument()
    {
        var args = MultiProcessDriver.BuildProcessArgs(
            "/tools/Chorus.LoadTool.dll", null, ["--platform", "test"]);

        Assert.Equal(["/tools/Chorus.LoadTool.dll", "--platform", "test"], args);
        Assert.Equal(["--platform", "test"], MultiProcessDriver.BuildProcessArgs(
            "/usr/bin/dotnet", null, ["--", "--platform", "test"]));
        Assert.Equal(["/tools/Chorus.LoadTool.dll", "--platform", "test"], MultiProcessDriver.BuildProcessArgs(
            "/usr/bin/dotnet", "/tools/Chorus.LoadTool.dll", ["--", "--platform", "test"]));

        var startInfo = MultiProcessDriver.BuildStartInfo(
            "/usr/bin/dotnet", "/tools/Chorus.LoadTool.dll",
            ["--", "--driver-processes", "2", "--platform", "test"], 0, 2, 15);

        Assert.Equal(11, startInfo.ArgumentList.Count);
        Assert.Equal("/tools/Chorus.LoadTool.dll", startInfo.ArgumentList[0]);
        Assert.Equal("15", startInfo.ArgumentList[8]);
    }

    [Fact]
    public void WsEndpoint_DerivesFromBaseUrl()
    {
        Assert.Equal("ws://127.0.0.1:5168/ws", LoadToolOptions.WsEndpoint("http://127.0.0.1:5168"));
        Assert.Equal("ws://127.0.0.1:5168/ws", LoadToolOptions.WsEndpoint("http://127.0.0.1:5168/"));
        Assert.Equal("wss://gw.example.com/ws", LoadToolOptions.WsEndpoint("https://gw.example.com"));
        Assert.Equal("wss://gw.example.com/ws", LoadToolOptions.WsEndpoint("https://gw.example.com/"));
        Assert.Equal("ws://127.0.0.1:5168/ws", LoadToolOptions.WsEndpoint("127.0.0.1:5168"));
    }

    [Fact]
    public void WsEndpoint_ExplicitUrlWins()
    {
        Assert.Equal("ws://127.0.0.1:5169/ws", LoadToolOptions.WsEndpoint("http://127.0.0.1:5168", "ws://127.0.0.1:5169/ws"));
        Assert.Equal("ws://127.0.0.1:5169/ws", LoadToolOptions.WsEndpoint("http://127.0.0.1:5168", "ws://127.0.0.1:5169/ws/"));
    }

    [Fact]
    public void Options_EffectiveWsUrlDerivesWhenUnset()
    {
        var options = LoadToolOptions.Parse(new[] { "--url", "http://127.0.0.1:5168" });

        Assert.Null(options.WsUrl);
        Assert.Equal("ws://127.0.0.1:5168/ws", options.EffectiveWsUrl);
    }

    [Fact]
    public void RatePacer_SubMillisecondIntervalsSurvive()
    {
        // Regression for the 1000/producerRps integer truncation: at 30k rps
        // over 8 producers each producer targets 3750/s (0.27ms per send) -
        // the old math rounded that to 0 and pacing silently vanished.
        var pacer = new RatePacer(3750);

        for (var i = 0; i < 100; i++)
        {
            pacer.RecordSend();
        }

        // 100 sends are scheduled across ~26.7ms; even allowing a few ms of
        // real time to pass while recording them, the delay must be positive.
        var delay = pacer.NextDelay();
        Assert.True(delay > TimeSpan.FromMilliseconds(1),
            $"expected a positive delay, got {delay.TotalMilliseconds}ms");
    }

    [Fact]
    public void RatePacer_FirstSendIsImmediate()
    {
        var pacer = new RatePacer(1);

        var delay = pacer.NextDelay();

        Assert.True(delay <= TimeSpan.FromMilliseconds(50),
            $"first send should not wait, got {delay.TotalMilliseconds}ms");
    }

    [Fact]
    public void RatePacer_BehindScheduleDegradesToZero()
    {
        var pacer = new RatePacer(1000); // 1ms per send

        pacer.RecordSend();
        Thread.Sleep(20); // far past the 1ms slot of the next send

        Assert.Equal(TimeSpan.Zero, pacer.NextDelay());
    }

    [Fact]
    public void Options_HasDefaults()
    {
        var options = LoadToolOptions.Parse(Array.Empty<string>());

        Assert.Equal("http://127.0.0.1:5168", options.BaseUrl);
        Assert.Equal("douyin", options.Platform);
        Assert.Equal(4, options.Sessions);
        Assert.Equal(50, options.EventsPerSecondTotal);
        Assert.Equal(3, options.ClientsPerSession);
        Assert.Equal(4, options.ProducerConnections);
        Assert.Equal("fast,normal,slow", options.AckMix);
        Assert.Equal(20, options.DisconnectEverySeconds);
        Assert.Equal(1000, options.ReconnectBackoffMs);
        Assert.Equal(60, options.DurationSeconds);
        Assert.Equal(5000, options.UserPoolSize);
        Assert.Equal(0.05, options.GiftRate);
        Assert.Equal(0.02, options.DuplicateRate);
        Assert.Null(options.MatchId);
        Assert.Null(options.SignSecret);
        Assert.Null(options.Token);
        Assert.Null(options.EngineMetricsUrl);
        Assert.Equal(5, options.ReportEverySeconds);
    }

    [Fact]
    public void Options_EnvironmentOverridesDefaults()
    {
        var previous = Environment.GetEnvironmentVariable("CHORUS_LOAD_URL");
        try
        {
            Environment.SetEnvironmentVariable("CHORUS_LOAD_URL", "http://10.0.0.2:5168");
            var options = LoadToolOptions.Parse(Array.Empty<string>());
            Assert.Equal("http://10.0.0.2:5168", options.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHORUS_LOAD_URL", previous);
        }
    }

    [Fact]
    public void Options_EnvironmentOverridesPlatformTokenAndEngineMetrics()
    {
        string[] names = ["CHORUS_LOAD_PLATFORM", "CHORUS_WS_TOKEN", "CHORUS_LOAD_ENGINE_METRICS"];
        var previous = names.ToDictionary(n => n, n => Environment.GetEnvironmentVariable(n));
        try
        {
            Environment.SetEnvironmentVariable("CHORUS_LOAD_PLATFORM", "kuaishou");
            Environment.SetEnvironmentVariable("CHORUS_WS_TOKEN", "env-tok");
            Environment.SetEnvironmentVariable("CHORUS_LOAD_ENGINE_METRICS", "http://10.0.0.3:9/metrics");

            // Environment wins: it is applied after the CLI flags so a scripted
            // CI run can redirect a flag-carrying command line.
            var options = LoadToolOptions.Parse(["--platform", "bilibili"]);

            Assert.Equal("kuaishou", options.Platform);
            Assert.Equal("env-tok", options.Token);
            Assert.Equal("http://10.0.0.3:9/metrics", options.EngineMetricsUrl);
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void Options_MissingValueThrows()
    {
        Assert.Throws<ArgumentException>(() => LoadToolOptions.Parse(["--url"]));
    }

    [Fact]
    public void Options_UnknownArgumentThrows()
    {
        Assert.Throws<ArgumentException>(() => LoadToolOptions.Parse(["--nope", "x"]));
    }

    [Fact]
    public void Options_ParsesSignFlag()
    {
        var options = LoadToolOptions.Parse(["--sign", "s3cr3t"]);

        Assert.Equal("s3cr3t", options.SignSecret);
    }

    [Fact]
    public void Options_SignMissingValueThrows()
    {
        Assert.Throws<ArgumentException>(() => LoadToolOptions.Parse(["--sign"]));
    }

    [Fact]
    public void Options_EnvironmentOverridesSignSecret()
    {
        const string name = "CHORUS_LOAD_DOUYIN_SECRET";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "env-secret");

            // Environment wins over the CLI flag, like the other load options:
            // a scripted CI run can redirect a flag-carrying command line.
            var options = LoadToolOptions.Parse(["--sign", "flag-secret"]);

            Assert.Equal("env-secret", options.SignSecret);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void PrintUsage_WritesOptionsToConsole()
    {
        var writer = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(writer);
            LoadToolOptions.PrintUsage();
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = writer.ToString();
        Assert.Contains("Usage: dotnet run --project src/Chorus.LoadTool", text);
        Assert.Contains("--url", text);
        Assert.Contains("--sign", text);
        Assert.Contains("--engine-metrics", text);
    }
}

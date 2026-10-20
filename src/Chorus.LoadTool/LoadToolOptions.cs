namespace Chorus.LoadTool;

/// <summary>
/// CLI options for the standalone load tool. Everything can also come from the
/// environment (CHORUS_*) so the same run can be scripted in CI.
/// </summary>
public sealed record LoadToolOptions(
    string BaseUrl = "http://127.0.0.1:5168",
    string Platform = "douyin",
    int Sessions = 4,
    int EventsPerSecondTotal = 50,
    int ClientsPerSession = 3,
    int ProducerConnections = 4,
    string AckMix = "fast,normal,slow",
    int DisconnectEverySeconds = 20,
    int ReconnectBackoffMs = 1000,
    int DurationSeconds = 60,
    int UserPoolSize = 5000,
    double GiftRate = 0.05,
    double DuplicateRate = 0.02,
    string? MatchId = null,
    string? SignSecret = null,
    string? Token = null,
    string? EngineMetricsUrl = null,
    int ReportEverySeconds = 5,
    string? WsUrl = null,
    int DriverProcesses = 1,
    int? DriverShardIndex = null,
    int? DriverShardCount = null,
    double? DriverEventsPerSecond = null,
    string EventNamespace = ""
)
{
    /// <summary>
    /// Resolves the streamer WS endpoint. Split-role topology puts the ingress face
    /// and the /ws face on different processes (Gateway:Roles=platform vs =streamer),
    /// so the WS URL cannot always be derived from the ingress base URL — pass it
    /// explicitly (WsUrl) in that case. Default keeps the historical single-host
    /// behaviour: http→ws, https→wss, bare host→ws.
    /// </summary>
    public string EffectiveWsUrl => string.IsNullOrWhiteSpace(WsUrl)
        ? WsEndpoint(BaseUrl)
        : WsUrl.TrimEnd('/');

    public double EffectiveEventsPerSecondTotal => DriverEventsPerSecond ?? EventsPerSecondTotal;

    public string EventIdPrefix => string.IsNullOrWhiteSpace(EventNamespace)
        ? string.Empty
        : EventNamespace.TrimEnd('-') + "-";

    public string ClientIdPrefix => EventIdPrefix;

    public string ShardLabel => DriverShardIndex is { } shardIndex
        ? $" shard{shardIndex + 1}"
        : string.Empty;

    /// <summary>
    /// True when this driver process owns the zero-based session slot. A
    /// single-process driver owns every slot; a multi-process child owns
    /// slot % shardCount == shardIndex, so the shards partition the session
    /// set (distinct rooms per shard) instead of every shard recreating the
    /// full set and multiplying the WS fan-out without lifting the rate.
    /// </summary>
    public bool OwnsShardIndex(int zeroBasedIndex) => DriverShardIndex is null
        || DriverShardCount is null
        || zeroBasedIndex % DriverShardCount.Value == DriverShardIndex.Value;

    public static string WsEndpoint(string baseUrl, string? explicitUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl.TrimEnd('/');
        }

        var u = baseUrl.TrimEnd('/');
        return u switch
        {
            var x when x.StartsWith("https:", StringComparison.OrdinalIgnoreCase) => "wss://" + x[8..] + "/ws",
            var x when x.StartsWith("http:", StringComparison.OrdinalIgnoreCase) => "ws://" + x[7..] + "/ws",
            _ => "ws://" + u + "/ws",
        };
    }

    public static LoadToolOptions Parse(string[] args)
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name);
        var o = new LoadToolOptions();

        for (var i = 0; i < args.Length; i++)
        {
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {args[i]}");
                }
                return args[++i];
            }

            switch (args[i])
            {
                case "--url": o = o with { BaseUrl = Next() }; break;
                case "--platform": o = o with { Platform = Next() }; break;
                case "--sessions": o = o with { Sessions = int.Parse(Next()) }; break;
                case "--rate": o = o with { EventsPerSecondTotal = int.Parse(Next()) }; break;
                case "--clients": o = o with { ClientsPerSession = int.Parse(Next()) }; break;
                case "--producers": o = o with { ProducerConnections = int.Parse(Next()) }; break;
                case "--ack-mix": o = o with { AckMix = Next() }; break;
                case "--disconnect-every": o = o with { DisconnectEverySeconds = int.Parse(Next()) }; break;
                case "--reconnect-ms": o = o with { ReconnectBackoffMs = int.Parse(Next()) }; break;
                case "--duration": o = o with { DurationSeconds = int.Parse(Next()) }; break;
                case "--users": o = o with { UserPoolSize = int.Parse(Next()) }; break;
                case "--gift-rate": o = o with { GiftRate = double.Parse(Next()) }; break;
                case "--dup-rate": o = o with { DuplicateRate = double.Parse(Next()) }; break;
                case "--match": o = o with { MatchId = Next() }; break;
                case "--sign": o = o with { SignSecret = Next() }; break;
                case "--token": o = o with { Token = Next() }; break;
                case "--engine-metrics": o = o with { EngineMetricsUrl = Next() }; break;
                case "--report-every": o = o with { ReportEverySeconds = int.Parse(Next()) }; break;
                case "--ws-url": o = o with { WsUrl = Next() }; break;
                case "--driver-processes": o = o with { DriverProcesses = int.Parse(Next()) }; break;
                case "--driver-shard-index": o = o with { DriverShardIndex = int.Parse(Next()) }; break;
                case "--driver-shard-count": o = o with { DriverShardCount = int.Parse(Next()) }; break;
                case "--driver-rate": o = o with { DriverEventsPerSecond = double.Parse(Next()) }; break;
                case "--event-namespace": o = o with { EventNamespace = Next() }; break;
                case "--help":
                case "-h":
                case "-?":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        o = o with
        {
            BaseUrl = Env("CHORUS_LOAD_URL") ?? o.BaseUrl,
            Platform = Env("CHORUS_LOAD_PLATFORM") ?? o.Platform,
            SignSecret = Env("CHORUS_LOAD_SIGN_SECRET") ?? Env("CHORUS_LOAD_DOUYIN_SECRET") ?? o.SignSecret,
            Token = Env("CHORUS_WS_TOKEN") ?? o.Token,
            EngineMetricsUrl = Env("CHORUS_LOAD_ENGINE_METRICS") ?? o.EngineMetricsUrl,
            WsUrl = Env("CHORUS_LOAD_WS_URL") ?? o.WsUrl,
        };

        if (o.DriverProcesses < 1)
        {
            throw new ArgumentException("--driver-processes must be at least 1");
        }
        if (o.DriverShardIndex.HasValue != o.DriverShardCount.HasValue)
        {
            throw new ArgumentException("--driver-shard-index and --driver-shard-count must be supplied together");
        }
        if (o.DriverShardIndex is { } index && o.DriverShardCount is { } count)
        {
            if (count < 1 || index < 0 || index >= count)
            {
                throw new ArgumentException("--driver-shard must be a zero-based index below --driver-shard-count");
            }
            if (o.DriverProcesses != 1)
            {
                throw new ArgumentException("driver shards cannot spawn nested driver processes");
            }
        }
        if (o.DriverEventsPerSecond is < 0)
        {
            throw new ArgumentException("--driver-rate must be at least zero");
        }

        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            Chorus load tool: drives audience-event ingress and simulated streamer
            WS clients (variable ack cadence, random disconnects) against a running
            Gateway.

            Usage: dotnet run --project src/Chorus.LoadTool [options]

            Options:
              --url <http://host:port>     Gateway base URL (default http://127.0.0.1:5168)
              --platform <name>            Ingress platform name (default douyin)
              --sessions <n>               Concurrent sessions (default 4)
              --rate <rps>                 Events/sec total across all sessions,
                                           split across producers (default 50)
              --clients <n>                WS clients per session (default 3)
              --producers <n>              Parallel HTTP producer connections (default 4)
              --ack-mix <fast,normal,slow> Ack cadence mix for simulated clients
              --disconnect-every <sec>     Mean seconds between client reconnects (default 20)
              --reconnect-ms <ms>          Backoff before reconnecting (default 1000)
              --duration <sec>             Run duration (default 60)
              --users <n>                  Simulated viewer pool size (default 5000)
              --gift-rate <0..1>           Fraction of events that are gifts (default 0.05)
              --dup-rate <0..1>            Fraction of duplicate event ids (default 0.02)
              --match <id>                 Optional match id shared by all sessions
              --sign <secret>              Sign ingress posts (douyin placeholder
                                           HMAC-SHA256, kuaishou sha1 scheme).
                                           Env: CHORUS_LOAD_SIGN_SECRET or
                                           CHORUS_LOAD_DOUYIN_SECRET
              --token <tok>                WS auth token (or CHORUS_WS_TOKEN)
              --engine-metrics <url>       Engine /metrics URL: scraped at the end, and
                                           every --report-every interval to report run peaks
              --report-every <sec>         Progress report interval (default 5)
              --ws-url <ws://host:port/ws> Streamer WS endpoint, when the /ws face
                                           sits on a different process than /ingress
                                           (split-role topology). Env:
                                           CHORUS_LOAD_WS_URL. Default: derived from
                                           --url (http→ws, https→wss, + "/ws")
              --driver-processes <n>       Run n load-tool processes. The parent
                                           splits rate and namespaces event/client
                                           ids so shards do not collide (default 1)
                                           Child output is labeled with its shard.
            """);
    }
}

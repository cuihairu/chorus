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
    int ReportEverySeconds = 5
)
{
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

        return o with
        {
            BaseUrl = Env("CHORUS_LOAD_URL") ?? o.BaseUrl,
            Platform = Env("CHORUS_LOAD_PLATFORM") ?? o.Platform,
            SignSecret = Env("CHORUS_LOAD_SIGN_SECRET") ?? Env("CHORUS_LOAD_DOUYIN_SECRET") ?? o.SignSecret,
            Token = Env("CHORUS_WS_TOKEN") ?? o.Token,
            EngineMetricsUrl = Env("CHORUS_LOAD_ENGINE_METRICS") ?? o.EngineMetricsUrl,
        };
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
            """);
    }
}

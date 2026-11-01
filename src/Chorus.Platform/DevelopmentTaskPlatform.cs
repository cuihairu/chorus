using System.Net;
using System.Text;
using System.Text.Json;

namespace Chorus.Platform;

/// <summary>
/// The development task platform's HTTP face: the endpoint
/// <see cref="DevelopmentPlatformTaskAdapter"/> speaks to. It records operations
/// idempotently by operation id and answers status queries — the one property the task
/// protocol depends on, an official record that survives the death of any executor — plus
/// the scriptable fault shapes the acceptance tests inject (a hanging answer, an apply that
/// never happened, a status face that cannot answer). Development-only; real platforms
/// bring their own adapter.
/// </summary>
public sealed class DevelopmentTaskPlatform : IDisposable
{
    /// <summary>One operation's platform behavior. The defaults are the honest platform:
    /// the call applies, the answer arrives, the status face answers the official
    /// record.</summary>
    public sealed class Script
    {
        public bool Apply = true;
        /// <summary>The reply never arrives in time: the operation applies (or not) and
        /// the caller's patience ends first — the outcome is the executor's unknown.</summary>
        public TimeSpan? HangAnswer;
        /// <summary>The status face cannot answer for the operation (answers 503).</summary>
        public bool StatusCannotAnswer;
    }

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _statusQueries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _executed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Script> _scripts = new(StringComparer.Ordinal);

    public string Url { get; }

    private DevelopmentTaskPlatform(int port) => Url = $"http://localhost:{port}/";

    /// <summary>Starts the platform on a free port; the caller may name its own port
    /// picker so test hosts and the demo cluster share one allocation discipline.</summary>
    public static DevelopmentTaskPlatform Start(Func<int>? freePort = null)
    {
        var platform = new DevelopmentTaskPlatform(freePort is null ? FreePort() : freePort());
        platform._listener.Prefixes.Add(platform.Url);
        platform._listener.Start();
        _ = platform.RunAsync();
        return platform;
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public int Calls(string operationId)
    {
        lock (_gate) return _calls.GetValueOrDefault(operationId);
    }

    /// <summary>The kind every received operation declared, by operation id — the
    /// evidence that a round end never carried an unbind or a live end.</summary>
    public IReadOnlyDictionary<string, string> Kinds
    {
        get { lock (_gate) return new Dictionary<string, string>(_kinds, StringComparer.Ordinal); }
    }

    public int StatusQueries(string operationId)
    {
        lock (_gate) return _statusQueries.GetValueOrDefault(operationId);
    }

    public void ScriptOperation(string operationId, Script script)
    {
        lock (_gate) _scripts[operationId] = script;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(context));
            }
        }
        catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod == "GET"
                && context.Request.Url!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                var queried = context.Request.QueryString["operationId"] ?? "";
                Script? script;
                bool executed;
                lock (_gate)
                {
                    _statusQueries[queried] = _statusQueries.GetValueOrDefault(queried) + 1;
                    script = _scripts.GetValueOrDefault(queried);
                    executed = _executed.GetValueOrDefault(queried);
                }
                if (script is { StatusCannotAnswer: true })
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }
                var statusReply = JsonSerializer.Serialize(new
                {
                    executed,
                    observedJson = executed ? """{"applied":true}""" : null as string
                }, JsonSerializerOptions.Web);
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(
                    Encoding.UTF8.GetBytes(statusReply), _stop.Token);
                context.Response.Close();
                return;
            }
            using var document = JsonDocument.Parse(await new StreamReader(context.Request.InputStream)
                .ReadToEndAsync(_stop.Token));
            var operationId = document.RootElement.GetProperty("operationId").GetString()!;
            var kind = document.RootElement.TryGetProperty("desired", out var desired)
                && desired.ValueKind == JsonValueKind.String
                && JsonDocument.Parse(desired.GetString()!).RootElement.TryGetProperty(
                    "kind", out var kindElement)
                && kindElement.ValueKind == JsonValueKind.String
                    ? kindElement.GetString()! : "unknown";
            Script? callScript;
            lock (_gate)
            {
                _calls[operationId] = _calls.GetValueOrDefault(operationId) + 1;
                _kinds[operationId] = kind;
                callScript = _scripts.GetValueOrDefault(operationId);
                // Idempotent by operation id: a replay reads the official record, the
                // apply itself never runs twice.
                if (!_executed.ContainsKey(operationId))
                    _executed[operationId] = callScript?.Apply ?? true;
            }
            if (callScript?.HangAnswer is { } hang)
            {
                // The operation stands applied (or not) on the platform while the caller's
                // patience ends first — the outcome only the status face can answer for.
                await Task.Delay(hang, _stop.Token);
            }
            bool applied;
            lock (_gate) applied = _executed[operationId];
            var reply = JsonSerializer.Serialize(new
            {
                executed = applied,
                observedJson = applied ? """{"applied":true}""" : null as string
            }, JsonSerializerOptions.Web);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(
                Encoding.UTF8.GetBytes(reply), _stop.Token);
            context.Response.Close();
        }
        catch (Exception)
        {
            // A half-open probe is the caller's problem; the record already stands.
        }
    }
}

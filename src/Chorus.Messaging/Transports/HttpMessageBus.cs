using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;

namespace Chorus.Messaging.Transports;

public sealed class HttpMessageBus : IMessageBus
{
    private readonly string _url;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly Channel<Envelope> _inbox = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });
    private readonly HttpClient _client = new();

    private HttpListener? _listener;
    private Task? _serverLoopTask;

    public HttpMessageBus(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Url is required.", nameof(url));
        }

        _url = url.EndsWith('/') ? url : $"{url}/";
    }

    public string Url => _url;

    public void StartServer()
    {
        if (_listener != null)
        {
            return;
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add(_url);
        _listener.Start();

        _serverLoopTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                _ = Task.Run(() => HandleRequestAsync(context), _cts.Token);
            }
        }, _cts.Token);
    }

    public async ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonEnvelopeCodec.Serialize(envelope);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await _client.PostAsync(_url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async IAsyncEnumerable<Envelope> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _inbox.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_inbox.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cts.Cancel();
        _inbox.Writer.TryComplete();

        if (_listener != null)
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _listener = null;
        }

        if (_serverLoopTask != null)
        {
            try { await _serverLoopTask; } catch { }
            _serverLoopTask = null;
        }

        _client.Dispose();
        _cts.Dispose();
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            using var ms = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(ms, _cts.Token);
            var env = JsonEnvelopeCodec.Deserialize(ms.ToArray());
            await _inbox.Writer.WriteAsync(env, _cts.Token);

            context.Response.StatusCode = (int)HttpStatusCode.Accepted;
            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }
}

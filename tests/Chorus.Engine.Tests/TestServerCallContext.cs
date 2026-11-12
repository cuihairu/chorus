using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace Chorus.Engine.Tests;

/// <summary>
/// The minimal ServerCallContext for driving gRPC service handlers as plain methods. The
/// handlers' certificate fence reads GetHttpContext() — a Grpc.AspNetCore extension that falls
/// back to the context's user state (its "__HttpContext" key) when the context is not the
/// ASP.NET Core-hosted one — so this context mounts a DefaultHttpContext carrying the caller's
/// client certificate, and the fence runs its real role/tenant logic without a server.
/// </summary>
internal sealed class TestServerCallContext : ServerCallContext
{
    /// <summary>The user-state key Grpc.AspNetCore's GetHttpContext extension reads.</summary>
    public static readonly string HttpContextKey = "__HttpContext";

    private static readonly DateTime DeadlineUtc = DateTime.UtcNow.AddMinutes(5);
    private readonly CancellationToken _cancellationToken;
    private readonly Metadata _requestHeaders = [];
    private readonly Metadata _responseTrailers = [];
    private readonly AuthContext _authContext = new("none", []);

    /// <summary>The ASP.NET Core call context the cert fence reads through.</summary>
    public DefaultHttpContext HttpContext { get; }

    public TestServerCallContext(X509Certificate2? clientCertificate,
        CancellationToken cancellationToken = default)
    {
        _cancellationToken = cancellationToken;
        HttpContext = BuildHttpContext(clientCertificate);
        UserState[HttpContextKey] = HttpContext;
    }

    /// <summary>Adds a request header the handler reads from the metadata — the operator
    /// bearer credential rides "Authorization" or "x-operator-token".</summary>
    public void AddHeader(string key, string value) => _requestHeaders.Add(key, value);

    private static DefaultHttpContext BuildHttpContext(X509Certificate2? clientCertificate)
    {
        var context = new DefaultHttpContext();
        context.Connection.ClientCertificate = clientCertificate;
        return context;
    }

    protected override string MethodCore => "/chorus.test/Test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "ipv4:127.0.0.1:9";
    protected override DateTime DeadlineCore => DeadlineUtc;
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => _cancellationToken;
    protected override Metadata ResponseTrailersCore => _responseTrailers;
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; } = new();
    protected override AuthContext AuthContextCore => _authContext;

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotSupportedException("Propagation is not exercised by the handlers under test.");

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}

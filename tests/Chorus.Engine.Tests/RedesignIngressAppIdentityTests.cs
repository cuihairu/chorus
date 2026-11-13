using System.Text;
using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.PlatformGateway;
using Chorus.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Chorus.Engine.Tests;

/// <summary>
/// The app header is the caller's claim, not an identity (architecture review C1, first
/// atomic item): when the mounted verifier's credentials prove an application identity,
/// a disagreeing <c>X-Chorus-App-Id</c> header is refused (403) before any ingest or
/// push can re-scope dedup and attribution downstream. A verifier that claims no
/// identity leaves the header's pre-existing meaning untouched, and the missing-header
/// 400 keeps its place ahead of verification.
/// </summary>
public sealed class RedesignIngressAppIdentityTests
{
    private const string Body = """{"sessionId":"live-a","eventId":"ev-1","kind":"Comment"}""";

    private sealed class TrustedVerifier(string? trustedAppId) : IPlatformNamedEventVerifier, IPlatformTrustedAppIdSource
    {
        public string PlatformName => PlatformNames.Simulator;

        public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers) => true;

        public string? GetTrustedAppId(string platform)
            => string.Equals(platform, PlatformNames.Simulator, StringComparison.OrdinalIgnoreCase)
                ? trustedAppId
                : null;
    }

    private sealed class PlainVerifier : IPlatformNamedEventVerifier
    {
        public string PlatformName => PlatformNames.Simulator;

        public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers) => true;
    }

    private sealed class ScriptedIngest : PlatformIngest.PlatformIngestClient
    {
        public int Calls;
        public readonly List<IngestBatchRequest> Seen = [];

        public override AsyncUnaryCall<IngestBatchReply> IngestBatchAsync(
            IngestBatchRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Seen.Add(request);
            var reply = new IngestBatchReply();
            if (request.Events.Count > 0)
            {
                reply.Receipts.Add(new EventReceipt
                {
                    SourceDedupKey = request.Events[0].SourceDedupKey,
                    Disposition = "Durable"
                });
            }
            return new AsyncUnaryCall<IngestBatchReply>(Task.FromResult(reply),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    private static async Task<(int Status, string? Error, ScriptedIngest Ingest)> PostAsync(
        IPlatformEventVerifier verifier, string? appId, string platform = PlatformNames.Simulator)
    {
        var ingest = new ScriptedIngest();
        var endpoint = new PlatformIngressEndpoint(verifier,
            [new SimulatorPlatformEventMapper()], ingest);
        var context = new DefaultHttpContext();
        // The typed results the face returns serialize through the request's JSON options —
        // an empty provider with the defaults is all they need.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<IOptions<JsonOptions>>(
            Microsoft.Extensions.Options.Options.Create(new JsonOptions()));
        services.ConfigureHttpJsonOptions(_ => { });
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        if (appId is not null)
        {
            context.Request.Headers[PlatformIngressEndpoint.AppIdHeader] = appId;
        }
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body));
        context.Response.Body = new MemoryStream();
        await (await endpoint.HandleAsync(platform, context)).ExecuteAsync(context);
        context.Response.Body.Position = 0;
        string? error = null;
        using (var document = JsonDocument.Parse(context.Response.Body))
        {
            if (document.RootElement.TryGetProperty("error", out var errorEl))
            {
                error = errorEl.GetString();
            }
        }
        return (context.Response.StatusCode, error, ingest);
    }

    [Fact]
    public async Task AppHeaderDisagreeingWithTrustedIdentityIsRefusedWithoutIngesting()
    {
        var (status, error, ingest) = await PostAsync(new TrustedVerifier("app-cred-1"), appId: "app-1");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("app_id_mismatch", error);
        Assert.Equal(0, ingest.Calls);
    }

    [Fact]
    public async Task AppHeaderMatchingTrustedIdentityFlowsThroughToIngest()
    {
        var (status, error, ingest) = await PostAsync(new TrustedVerifier("app-cred-1"), appId: "app-cred-1");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Null(error);
        Assert.Equal(1, ingest.Calls);
    }

    [Fact]
    public async Task VerifierClaimingNoIdentityKeepsTheHeaderTrust()
    {
        var (status, error, ingest) = await PostAsync(new PlainVerifier(), appId: "whatever-app");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Null(error);
        Assert.Equal(1, ingest.Calls);
    }

    // Ingress normalization (review m1): the batch stored in the data plane carries the
    // canonical spelling — platform label and operation id — whatever casing the URL had.
    [Fact]
    public async Task MixedCasePlatformIsNormalizedBeforeStorage()
    {
        var (status, error, ingest) = await PostAsync(new PlainVerifier(), appId: "whatever-app",
            platform: "SIMULATOR");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Null(error);
        var request = Assert.Single(ingest.Seen);
        Assert.Equal("simulator", request.Platform);
        Assert.StartsWith("ingress:simulator:", request.OperationId);
    }

    [Fact]
    public async Task MissingAppHeaderStaysBadRequestEvenForTrustedVerifiers()
    {
        var (status, error, ingest) = await PostAsync(new TrustedVerifier("app-cred-1"), appId: null);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("missing_app_id", error);
        Assert.Equal(0, ingest.Calls);
    }
}

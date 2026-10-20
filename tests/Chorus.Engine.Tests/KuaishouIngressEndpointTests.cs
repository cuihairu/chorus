using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Golden tests for the Kuaishou ingress pipeline over the real HTTP endpoint
/// (official-sample style payloads: valid signature accepted, tampered
/// rejected, duplicate msgIds deduplicated, unknown event types do not
/// crash). Signature (sha1Hex(body+token), "kwaisign" header) and the AES
/// envelope follow the official contract in docs/reference/platform-kuaishou.md;
/// the inner event JSON is the placeholder contract pending joint debugging.
/// </summary>
public sealed class KuaishouIngressEndpointTests : IClassFixture<KuaishouIngressEndpointTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:Kuaishou:VerifyToken", "e2e-ks-token");
            builder.UseSetting("Ingress:Kuaishou:MsgEncryptKey", TestKey);
            builder.UseSetting("Ingress:DedupTtlSeconds", "300");
        }
    }

    private const string TestKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private readonly Factory _factory;

    public KuaishouIngressEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    /// <summary>Builds an official-style Kuaishou callback: AES-encrypted inner JSON inside the envelope, sha1-signed body.</summary>
    private static (string Body, string Signature) BuildCallback(string msgId, string innerJson)
    {
        var key = Convert.FromBase64String(TestKey);
        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = key.AsSpan(0, 16).ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(innerJson), 0, Encoding.UTF8.GetByteCount(innerJson));
        }

        var body = JsonSerializer.Serialize(new
        {
            encryptedMsg = Convert.ToBase64String(cipher),
            msgId,
            componentAppId = "ks-app",
            timestamp = 1700000000000L,
        });

        using var sha1 = SHA1.Create();
        var signature = Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(body + "e2e-ks-token"))).ToLowerInvariant();
        return (body, signature);
    }

    private static HttpResponseMessage PostKuaishou(HttpClient client, string body, string? signature = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingress/kuaishou")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("kwaisign", signature ?? string.Empty);
        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Ingress_CorrectSignature_Accepts()
    {
        var client = _factory.CreateClient();
        var (body, signature) = BuildCallback("e2e-ks-1", """{"event":"comment","session_id":"ks-e2e-1","user_id":"u1","content":"hi"}""");

        using var response = PostKuaishou(client, body, signature);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("ks-e2e-1", payload.GetProperty("sessionId").GetString());
        Assert.Equal("e2e-ks-1", payload.GetProperty("eventId").GetString());
    }

    [Fact]
    public void Ingress_TamperedBody_Rejected()
    {
        var client = _factory.CreateClient();
        var (body, signature) = BuildCallback("e2e-ks-2", """{"event":"comment","session_id":"ks-e2e-2","user_id":"u1","content":"hi"}""");
        var tampered = body.Replace("e2e-ks-2", "attacked");

        using var response = PostKuaishou(client, tampered, signature);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Ingress_WrongToken_Rejected()
    {
        var client = _factory.CreateClient();
        var (body, _) = BuildCallback("e2e-ks-3", """{"event":"comment","session_id":"ks-e2e-3","user_id":"u1"}""");

        using var response = PostKuaishou(client, body, signature: "0000000000000000000000000000000000000000");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Ingress_MissingSignature_Rejected()
    {
        var client = _factory.CreateClient();
        var (body, _) = BuildCallback("e2e-ks-4", """{"event":"comment","session_id":"ks-e2e-4","user_id":"u1"}""");

        using var response = PostKuaishou(client, body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingress_DuplicateMsgId_Deduplicated()
    {
        var client = _factory.CreateClient();
        var (body, signature) = BuildCallback("e2e-ks-dup", """{"event":"like","session_id":"ks-e2e-5","user_id":"u1"}""");

        using var first = PostKuaishou(client, body, signature);
        using var second = PostKuaishou(client, body, signature);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var payload = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("duplicated").GetBoolean());
    }

    [Fact]
    public async Task Ingress_UnknownEventType_DoesNotCrash()
    {
        var client = _factory.CreateClient();
        var (body, signature) = BuildCallback("e2e-ks-6", """{"event":"follow","session_id":"ks-e2e-6","user_id":"u1"}""");

        using var response = PostKuaishou(client, body, signature);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Ingress_UndecryptablePayload_Unmapped()
    {
        var client = _factory.CreateClient();
        // Valid signature over a body whose encryptedMsg does not decrypt with the configured key.
        var body = """{"encryptedMsg":"AAAA","msgId":"e2e-ks-7","componentAppId":"ks-app","timestamp":1700000000000}""";
        using var sha1 = SHA1.Create();
        var signature = Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(body + "e2e-ks-token"))).ToLowerInvariant();

        using var response = PostKuaishou(client, body, signature);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Ingress_MalformedBodyWithValidSignature_Unmapped()
    {
        var client = _factory.CreateClient();
        using var sha1 = SHA1.Create();
        var signature = Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes("not-json" + "e2e-ks-token"))).ToLowerInvariant();

        using var response = PostKuaishou(client, "not-json", signature);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

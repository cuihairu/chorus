using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Chorus.ControlServer;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The browser Admin face (FIX-WEB-03): the same Admin service, exposed over the gRPC JSON
/// transcoding on the no-client-certificate endpoint a browser can reach. The operator bearer
/// credential is the only authentication there (a browser presents no process certificate),
/// the who-am-i route is the login probe, and the role table is enforced over the real HTTP
/// surface: 401 without a credential, 403 for a role below the command's floor, the viewer
/// read floor (get-operation-status relaxed from gm) and the forged-identity rejection.
/// </summary>
public sealed class RedesignWeb03BrowserAdminHostTests
{
    [Fact]
    public async Task TheBrowserFaceAuthenticatesOperatorsAndEnforcesTheRoleTable()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        var operators = new RedesignOperatorStore(environment.DatabaseConnectionString);
        await operators.RegisterOperatorAsync("gm-alice", "gm", [], [], "alice-token");
        await operators.RegisterOperatorAsync("viewer-carl", "viewer", [], [], "carl-token");

        using var client = BrowserClient(environment);
        var baseUrl = $"https://localhost:{environment.BrowserPort}/admin/api/v1";

        // (1) The login probe answers the caller's own identity from the credential.
        using (var whoami = await PostAsync(client, baseUrl + "/who-am-i", "alice-token", "{}"))
        {
            Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
            var body = await whoami.Content.ReadAsStringAsync();
            Assert.Contains("\"operatorId\":\"gm-alice\"", body);
            Assert.Contains("\"role\":\"gm\"", body);
            Assert.Contains("\"enabled\":true", body);
        }

        // (2) No credential and (3) an unknown credential are refused before any read.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, baseUrl + "/who-am-i", null, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, baseUrl + "/who-am-i", "no-such-token", "{}")).StatusCode);

        // (4) A viewer read answers the room lookup (no room: found=false, not an auth error).
        using (var read = await PostAsync(client, baseUrl + "/get-room-snapshot", "carl-token",
            """{"roomId":"room-web03-none"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Contains("\"found\":false", await read.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, baseUrl + "/get-room-snapshot", null,
            """{"roomId":"room-web03-none"}""")).StatusCode);

        // (5) A viewer role is below the gm command floor: 403.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, baseUrl + "/suspend-match-queue", "carl-token",
            """{"requestId":"web03-gate-1","operator":"viewer-carl","reason":"viewer attempt","partitionKey":"test/web03"}""")).StatusCode);

        // (6) The gm command acts over the browser face.
        using (var suspend = await PostAsync(client, baseUrl + "/suspend-match-queue", "alice-token",
            """{"requestId":"web03-gate-2","operator":"gm-alice","reason":"incident web03","partitionKey":"test/web03"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
            Assert.Contains("\"suspended\":true", await suspend.Content.ReadAsStringAsync());
        }

        // (7) A forged body identity is refused even with a valid gm credential.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, baseUrl + "/suspend-match-queue", "alice-token",
            """{"requestId":"web03-gate-3","operator":"someone-else","reason":"forged","partitionKey":"test/web03"}""")).StatusCode);

        // (8) The status query relaxed from gm to viewer (FIX-WEB-01): a viewer reads it.
        using (var status = await PostAsync(client, baseUrl + "/get-operation-status", "carl-token",
            """{"operationId":"web03-op-none","tenantId":"tenant-a","gameId":"game-a","roomId":"room-web03-none"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.Contains("\"found\":false", await status.Content.ReadAsStringAsync());
        }

        // (9) The browser health word is who-am-i; the process-cert health word (Ready) is
        // deliberately not annotated — it is refused by the transcoding as an unknown route.
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, baseUrl + "/ready", "alice-token", "{}")).StatusCode);
    }

    /// <summary>The no-certificate HTTP client a browser would use. The hosts serve dynamic
    /// test certificates chaining to the per-test root CA, so validation is accepted here —
    /// the browser face's authentication is the operator bearer, not the server chain.</summary>
    private static HttpClient BrowserClient(RedesignM0HostTests.HostEnvironment environment) =>
        new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url,
        string? token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
}

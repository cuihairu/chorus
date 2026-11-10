using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Api;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The FIX-WEB-17 acceptance: the whole authorization table, walked route by route against
/// live hosts over the browser port. Anonymous callers are 401 everywhere with a well-formed
/// body (so the authentication gate is what answers — validation runs ahead of it on this
/// face); the role floor holds on every route (declared-viewer reads answer, everything at
/// or above the gm floor — every command and the epoch lookup GM-06 floored at gm — refuses
/// a viewer); every gm command reaches its business logic, whatever that answer is; a forged
/// body identity never rides a valid credential; and an unknown key answers the route's own
/// honest shape (found=false, an empty page) instead of an error or a folded yes. The roles
/// come from the WEB-01 contract table and the probe bodies from this file; the closure fact
/// pins the two together and to the compiled descriptors, so a future route cannot skip the
/// walk.
/// </summary>
public sealed class RedesignWeb17AdminAcceptanceHostTests
{
    // The probe bodies, one per contract route: $id becomes a fresh request id, $actor the
    // walking operator. Read probes name unknown keys on purpose — the unknown answers are
    // part of the acceptance. Command probes carry the audited triple and a throwaway
    // tenant/game so a walk never disturbs anything real.
    private static readonly Dictionary<string, string> ProbeBodies = new()
    {
        ["/admin/api/v1/who-am-i"] = "{}",
        ["/admin/api/v1/get-streamer-snapshot"] =
            """{"tenantId":"tenant-web17","streamerId":"nobody"}""",
        ["/admin/api/v1/get-room-snapshot"] =
            """{"roomId":"room-web17-none"}""",
        ["/admin/api/v1/trace-round"] =
            """{"roomId":"room-web17-none"}""",
        ["/admin/api/v1/get-room-epoch"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","tenantId":"tenant-web17","gameId":"game-web17","roomId":"room-web17-none"}""",
        ["/admin/api/v1/get-operation-status"] =
            """{"operationId":"op-web17-none","tenantId":"","gameId":"","roomId":""}""",
        ["/admin/api/v1/get-audit-trail"] =
            """{"command":"no-such-command","operator":"","maxEntries":10,"pageToken":""}""",
        ["/admin/api/v1/get-leaderboard-top"] =
            """{"tenantId":"tenant-web17","gameId":"game-web17","board":"score","periodId":"","scopeId":"","subject":0,"maxEntries":10,"pageToken":""}""",
        ["/admin/api/v1/get-leaderboard-rank"] =
            """{"tenantId":"tenant-web17","gameId":"game-web17","board":"score","periodId":"","scopeId":"","subject":0,"subjectId":"nobody"}""",
        ["/admin/api/v1/get-leaderboard-around"] =
            """{"tenantId":"tenant-web17","gameId":"game-web17","board":"score","periodId":"","scopeId":"","subject":0,"subjectId":"nobody","window":1}""",
        ["/admin/api/v1/adjust-leaderboard-entry"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","tenantId":"tenant-web17","gameId":"game-web17","board":"score","periodId":"","scopeId":"","subject":0,"subjectId":"web17-walk","delta":1,"expectedVersion":""}""",
        ["/admin/api/v1/reset-leaderboard-entry"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","tenantId":"tenant-web17","gameId":"game-web17","board":"score","periodId":"","scopeId":"","subject":0,"subjectId":"web17-nobody"}""",
        ["/admin/api/v1/rebuild-leaderboard"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","tenantId":"tenant-web17","gameId":"game-web17","board":"score","periodId":"","scopeId":""}""",
        ["/admin/api/v1/suspend-match-queue"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","partitionKey":"tenant-web17/game-web17"}""",
        ["/admin/api/v1/resume-match-queue"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","partitionKey":"tenant-web17/game-web17"}""",
        ["/admin/api/v1/retry-room-recovery"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","tenantId":"tenant-web17","gameId":"game-web17","roomId":"room-web17-none","toInstanceId":"game-web17-ghost","tickMs":25}""",
        ["/admin/api/v1/drain-process"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","processInstanceId":"proc-web17-none"}""",
        ["/admin/api/v1/retry-settlement"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","roomId":"room-web17-none"}""",
        ["/admin/api/v1/reconcile-platform-task"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","taskId":"task-web17-none"}""",
        ["/admin/api/v1/bump-room-epoch"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","tenantId":"tenant-web17","gameId":"game-web17","roomId":"room-web17-none","expectedEpoch":1}""",
        ["/admin/api/v1/dispatch-room-command"] =
            """{"requestId":"$id","operator":"$actor","reason":"acceptance walk","tenantId":"tenant-web17","gameId":"game-web17","roomId":"room-web17-none","participantId":"p-web17","payloadJson":"{}"}""",
    };

    private static string ProbeBody(string path, string requestId, string actor) =>
        ProbeBodies[path].Replace("$id", requestId).Replace("$actor", actor);

    private static string FreshId() => Guid.NewGuid().ToString("N");

    /// <summary>The walk covers the contract exactly: the WEB-01 table is closed over the
    /// compiled descriptors (re-pinned here) and every route has exactly one probe body, so
    /// a route added to the proto without a probe fails before any host starts.</summary>
    [Fact]
    public void TheAcceptanceWalkCoversEveryAnnotatedRoute()
    {
        var admin = Chorus.Rpc.M3Reflection.Descriptor.Services
            .Single(s => s.FullName == "chorus.m3.Admin");
        var annotated = admin.Methods
            .Where(m => m.GetOptions()?.HasExtension(AnnotationsExtensions.Http) == true)
            .Select(m => m.GetOptions()!.GetExtension(AnnotationsExtensions.Http).Post)
            .ToHashSet();
        var contract = RedesignWeb01AdminContractTests.ContractRoutes()
            .Select(r => r.Path).ToHashSet();

        Assert.Equal(annotated, contract);
        Assert.Equal(contract, ProbeBodies.Keys.ToHashSet());
    }

    [Fact]
    public async Task TheAuthorizationTableHoldsRouteByRouteAndUnknownKeysAnswerHonestly()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        // Both walkers carry no scope arrays: the floors under test are role floors, not
        // scope rejections (the scope cut is GM-06's own territory).
        var operators = new Chorus.ControlServer.RedesignOperatorStore(environment.DatabaseConnectionString);
        await operators.RegisterOperatorAsync("gm-alice", "gm", [], [], "alice-token");
        await operators.RegisterOperatorAsync("viewer-carl", "viewer", [], [], "carl-token");

        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        var baseUrl = $"https://localhost:{environment.BrowserPort}";

        foreach (var (path, kind, role) in RedesignWeb01AdminContractTests.ContractRoutes())
        {
            var url = baseUrl + path;

            // (1) No credential at all: 401 before any business — the body is well-formed
            // so the authentication gate is what answers.
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, url, null,
                ProbeBody(path, FreshId(), "gm-alice"))).StatusCode);

            // (2) The viewer: a declared-viewer read answers (with its unknown-key shape);
            // anything at or above the gm floor refuses with 403.
            using (var viewer = await PostAsync(client, url, "carl-token",
                ProbeBody(path, FreshId(), "viewer-carl")))
            {
                if (role == "viewer")
                {
                    Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
                    await AssertUnknownAnswerAsync(path, viewer);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
                }
            }

            // (3) The gm: the floor lets every command through to its business logic —
            // whatever the answer (a suspended gate, an unknown target, a rejected board
            // key), it is never an authentication or floor answer. The queue-gate pair
            // asserts its recorded values; the epoch lookup answers the unknown room.
            using (var gm = await PostAsync(client, url, "alice-token",
                ProbeBody(path, FreshId(), "gm-alice")))
            {
                Assert.NotEqual(HttpStatusCode.Unauthorized, gm.StatusCode);
                Assert.NotEqual(HttpStatusCode.Forbidden, gm.StatusCode);
                if (path.EndsWith("/get-room-epoch"))
                {
                    Assert.Equal(HttpStatusCode.OK, gm.StatusCode);
                    using var reply = JsonDocument.Parse(await gm.Content.ReadAsStringAsync());
                    Assert.False(reply.RootElement.GetProperty("found").GetBoolean());
                }
                if (path.EndsWith("/suspend-match-queue"))
                {
                    Assert.Equal(HttpStatusCode.OK, gm.StatusCode);
                    using var reply = JsonDocument.Parse(await gm.Content.ReadAsStringAsync());
                    Assert.True(reply.RootElement.GetProperty("suspended").GetBoolean());
                }
                if (path.EndsWith("/resume-match-queue"))
                {
                    Assert.Equal(HttpStatusCode.OK, gm.StatusCode);
                    using var reply = JsonDocument.Parse(await gm.Content.ReadAsStringAsync());
                    Assert.False(reply.RootElement.GetProperty("suspended").GetBoolean());
                }
            }

            // (4) A forged body identity never rides a valid credential: 403 on every route
            // that carries the audited triple — every command and the epoch lookup.
            if (kind == "command" || path.EndsWith("/get-room-epoch"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, url, "alice-token",
                    ProbeBody(path, FreshId(), "someone-else"))).StatusCode);
            }
        }
    }

    /// <summary>The unknown-key answers, per read route: found=false where the route has a
    /// found word, an empty page where it pages, and the walker's own identity for the login
    /// probe — never an error, never a folded yes.</summary>
    private static async Task AssertUnknownAnswerAsync(string path, HttpResponseMessage response)
    {
        using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = reply.RootElement;
        switch (path)
        {
            case "/admin/api/v1/who-am-i":
                Assert.Equal("viewer-carl", root.GetProperty("operatorId").GetString());
                Assert.Equal("viewer", root.GetProperty("role").GetString());
                Assert.True(root.GetProperty("enabled").GetBoolean());
                break;
            case "/admin/api/v1/get-audit-trail":
            case "/admin/api/v1/get-leaderboard-top":
                Assert.Equal(0, root.GetProperty("entries").GetArrayLength());
                Assert.False(root.GetProperty("hasMore").GetBoolean());
                break;
            default:
                Assert.False(root.GetProperty("found").GetBoolean());
                break;
        }
    }

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

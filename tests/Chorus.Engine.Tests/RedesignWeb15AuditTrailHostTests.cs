using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The audit trail query face (FIX-WEB-15) end to end over the browser port: the control
/// plane's own operation log — newest first, one exact-word filter per dimension (command,
/// operator), a bounded page, and an audit-id keyset cursor (append-only log, no snapshot
/// deadline: a newer row can never reorder an older one) — reachable as JSON with the
/// operator bearer alone, on the viewer floor like the single-id status query.
/// </summary>
public sealed class RedesignWeb15AuditTrailHostTests
{
    [Fact]
    public async Task TheBrowserFaceWalksTheAuditTrailNewestFirstWithFilters()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        var operators = new Chorus.ControlServer.RedesignOperatorStore(environment.DatabaseConnectionString);
        await operators.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "alice-token");
        await operators.RegisterOperatorAsync("watcher", "viewer", ["tenant-a"], ["game-a"], "watcher-token");

        // Five audit rows in a known order (the identity column preserves insertion
        // order, so newest first is exactly reverse insertion order).
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            var seed = new NpgsqlCommand("""
                INSERT INTO control.audit(request_id,operator,reason,scope,command,detail)
                VALUES ('req-1','gm-alice','open the gate','tenant-a/game-a','suspend-match-queue','{"partition":"p1"}'),
                       ('req-2','gm-bob','bump the epoch','tenant-a/game-a/room-9','room-epoch-bump','{"newEpoch":2}'),
                       ('req-3','gm-alice','wipe a cheat','score-alltime/alltime/global','leaderboard.reset','{"subjectId":"c"}'),
                       ('req-4','gm-bob','close the gate','tenant-a/game-a','resume-match-queue','{"partition":"p1"}'),
                       ('req-5','gm-alice','correct a score','score-alltime/alltime/global','leaderboard.adjust','{"subjectId":"a","delta":2}');
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }

        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        var baseUrl = $"https://localhost:{environment.BrowserPort}/admin/api/v1";

        // (1) The first page: newest first (reverse insertion), bounded, with the keyset
        // token exactly when more remain. The row fields ride verbatim — the audit's own
        // scope word and detail JSON, and the unix-ms time.
        string token;
        using (var page = await PostAsync(client, baseUrl + "/get-audit-trail", "alice-token",
            """{"command":"","operator":"","maxEntries":2,"pageToken":""}"""))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var reply = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
            var entries = reply.RootElement.GetProperty("entries");
            Assert.Equal(2, entries.GetArrayLength());
            Assert.Equal("req-5", entries[0].GetProperty("requestId").GetString());
            Assert.Equal("leaderboard.adjust", entries[0].GetProperty("command").GetString());
            Assert.Equal("score-alltime/alltime/global", entries[0].GetProperty("scope").GetString());
            Assert.Equal("correct a score", entries[0].GetProperty("reason").GetString());
            Assert.Equal("gm-alice", entries[0].GetProperty("operator").GetString());
            Assert.True(entries[0].GetProperty("createdAtUnixMs").GetInt64() > 0);
            Assert.Equal("req-4", entries[1].GetProperty("requestId").GetString());
            Assert.True(reply.RootElement.GetProperty("hasMore").GetBoolean());
            token = reply.RootElement.GetProperty("nextPageToken").GetString()!;
            Assert.True(token.Length > 0);
        }

        // (2) The walk: the token continues strictly older, and the whole log is covered
        // exactly once in order — req-3, req-2, then req-1 with hasMore=false and no token.
        var walked = new List<string> { "req-5", "req-4" };
        while (true)
        {
            using var page = await PostAsync(client, baseUrl + "/get-audit-trail", "alice-token",
                $$"""{"command":"","operator":"","maxEntries":2,"pageToken":"{{token}}"}""");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var reply = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
            foreach (var entry in reply.RootElement.GetProperty("entries").EnumerateArray())
                walked.Add(entry.GetProperty("requestId").GetString()!);
            if (!reply.RootElement.GetProperty("hasMore").GetBoolean())
            {
                Assert.Equal("", reply.RootElement.GetProperty("nextPageToken").GetString());
                break;
            }
            token = reply.RootElement.GetProperty("nextPageToken").GetString()!;
        }
        Assert.Equal(["req-5", "req-4", "req-3", "req-2", "req-1"], walked);

        // (3) The exact-word filters: a command names only its own rows; an operator
        // names only their own; an unknown word answers an empty page, not an error.
        using (var byCommand = await PostAsync(client, baseUrl + "/get-audit-trail", "alice-token",
            """{"command":"leaderboard.adjust","operator":"","maxEntries":10,"pageToken":""}"""))
        {
            Assert.Equal(HttpStatusCode.OK, byCommand.StatusCode);
            using var reply = JsonDocument.Parse(await byCommand.Content.ReadAsStringAsync());
            var entries = reply.RootElement.GetProperty("entries");
            Assert.Equal(1, entries.GetArrayLength());
            Assert.Equal("req-5", entries[0].GetProperty("requestId").GetString());
        }
        using (var byOperator = await PostAsync(client, baseUrl + "/get-audit-trail", "alice-token",
            """{"command":"","operator":"gm-bob","maxEntries":10,"pageToken":""}"""))
        {
            Assert.Equal(HttpStatusCode.OK, byOperator.StatusCode);
            using var reply = JsonDocument.Parse(await byOperator.Content.ReadAsStringAsync());
            var entries = reply.RootElement.GetProperty("entries");
            Assert.Equal(2, entries.GetArrayLength());
            Assert.Equal("req-4", entries[0].GetProperty("requestId").GetString());
            Assert.Equal("req-2", entries[1].GetProperty("requestId").GetString());
        }
        using (var none = await PostAsync(client, baseUrl + "/get-audit-trail", "alice-token",
            """{"command":"no-such-command","operator":"","maxEntries":10,"pageToken":""}"""))
        {
            Assert.Equal(HttpStatusCode.OK, none.StatusCode);
            using var reply = JsonDocument.Parse(await none.Content.ReadAsStringAsync());
            Assert.Equal(0, reply.RootElement.GetProperty("entries").GetArrayLength());
            Assert.False(reply.RootElement.GetProperty("hasMore").GetBoolean());
        }

        // (4) The floor and the bounds: a viewer reads; an anonymous caller is refused;
        // an unbounded page is a visible bad request.
        using (var viewer = await PostAsync(client, baseUrl + "/get-audit-trail", "watcher-token",
            """{"command":"","operator":"","maxEntries":2,"pageToken":""}"""))
        {
            Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        }
        using (var anonymous = await PostAsync(client, baseUrl + "/get-audit-trail", "",
            """{"command":"","operator":"","maxEntries":2,"pageToken":""}"""))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        using (var unbounded = await PostAsync(client, baseUrl + "/get-audit-trail", "alice-token",
            """{"command":"","operator":"","maxEntries":201,"pageToken":""}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unbounded.StatusCode);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url,
        string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (token.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
}

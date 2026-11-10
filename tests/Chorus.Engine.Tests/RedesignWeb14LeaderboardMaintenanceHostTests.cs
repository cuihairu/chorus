using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The back-office leaderboard maintenance face (FIX-WEB-14) end to end over the browser
/// port: the RANK-08 GM corrections — adjust one subject's entry, reset one subject or the
/// whole board, rebuild the projection from the effect ledger — reachable as JSON on the gm
/// floor, each leading with the audited triple and carrying the opaque read version a
/// query reply named. The version guard refuses a board that changed since the operator's
/// read (a precondition failure, HTTP 400 with the reload word), the operation id is the
/// idempotency key (a replay answers the stored score as a duplicate), a correction is a
/// ledger row (so the rebuild reproduces it), and the descriptor defaults ride exactly as
/// the queries spell them.
/// </summary>
public sealed class RedesignWeb14LeaderboardMaintenanceHostTests
{
    [Fact]
    public async Task TheBrowserFaceAdjustsResetsAndRebuildsUnderTheVersionGuard()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        var operators = new Chorus.ControlServer.RedesignOperatorStore(environment.DatabaseConnectionString);
        await operators.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "alice-token");
        await operators.RegisterOperatorAsync("watcher", "viewer", ["tenant-a"], ["game-a"], "watcher-token");

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO game.leaderboard_entry(tenant_id,game_id,board_id,period_id,scope_id,
                    subject_type,subject_id,score)
                VALUES ('tenant-a','game-a','score-alltime','alltime','global',0,'a',5),
                       ('tenant-a','game-a','score-alltime','alltime','global',0,'b',3),
                       ('tenant-a','game-a','score-alltime','alltime','global',0,'c',5);
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }

        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        var baseUrl = $"https://localhost:{environment.BrowserPort}/admin/api/v1";
        // The read key, brace-less: commands interpolate it mid-object after the audited
        // triple (the reads wrap it in braces themselves).
        const string key = """
            "tenantId":"tenant-a","gameId":"game-a","board":"score-alltime",
             "periodId":"","scopeId":"","subject":0
            """;

        // (1) The version token flows from a read: the top page's read_version is the
        // opaque string the commands name as expected_version (the unix-ms field is the
        // display form; the string is the lossless one).
        string version;
        using (var page = await PostAsync(client, baseUrl + "/get-leaderboard-top", "alice-token",
            $$"""
            {{{key}},"maxEntries":10,"pageToken":""}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var reply = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
            version = reply.RootElement.GetProperty("readVersion").GetString()!;
            Assert.True(version.Length > 0);
            Assert.True(reply.RootElement.GetProperty("readVersionUnixMs").GetInt64() > 0);
        }

        // (2) The adjustment: the audited triple leads, expected_version names the read's
        // token, and the answer carries the resulting score and the board's new version —
        // itself a fresh token.
        string adjustedVersion;
        using (var adjust = await PostAsync(client, baseUrl + "/adjust-leaderboard-entry", "alice-token",
            $$"""
            {"requestId":"op-web14-1","operator":"gm-alice","reason":"reward correction",
             {{key}},"subjectId":"a","delta":2,"expectedVersion":"{{version}}"}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, adjust.StatusCode);
            using var reply = JsonDocument.Parse(await adjust.Content.ReadAsStringAsync());
            Assert.Equal(7, reply.RootElement.GetProperty("score").GetInt64());
            Assert.False(reply.RootElement.GetProperty("duplicate").GetBoolean());
            adjustedVersion = reply.RootElement.GetProperty("readVersion").GetString()!;
            Assert.True(adjustedVersion.Length > 0);
            Assert.NotEqual(version, adjustedVersion);
        }

        // (3) The replay: the same operation id answers the stored score as a duplicate —
        // never a second delta. (The retry carries the version the operator re-read after
        // the lost response; the version guard runs before the idempotency claim.)
        using (var replay = await PostAsync(client, baseUrl + "/adjust-leaderboard-entry", "alice-token",
            $$"""
            {"requestId":"op-web14-1","operator":"gm-alice","reason":"reward correction",
             {{key}},"subjectId":"a","delta":2,"expectedVersion":"{{adjustedVersion}}"}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var reply = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(7, reply.RootElement.GetProperty("score").GetInt64());
            Assert.True(reply.RootElement.GetProperty("duplicate").GetBoolean());
        }

        // (4) The version guard: a correction naming the stale token is refused before any
        // write — an explicit precondition failure (HTTP 400 on the browser face) with the
        // reload word, never a correction onto a stale view.
        using (var stale = await PostAsync(client, baseUrl + "/adjust-leaderboard-entry", "alice-token",
            $$"""
            {"requestId":"op-web14-2","operator":"gm-alice","reason":"stale attempt",
             {{key}},"subjectId":"a","delta":1,"expectedVersion":"{{version}}"}
            """))
        {
            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.Contains("reload the board", await stale.Content.ReadAsStringAsync());
        }

        // (5) The bounds refusal: a correction below the board's floor is refused before
        // any write — the data plane never invents or folds an amount.
        using (var bounds = await PostAsync(client, baseUrl + "/adjust-leaderboard-entry", "alice-token",
            $$"""
            {"requestId":"op-web14-3","operator":"gm-alice","reason":"negative attempt",
             {{key}},"subjectId":"a","delta":-100,"expectedVersion":"{{adjustedVersion}}"}
            """))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bounds.StatusCode);
        }

        // (6) The subject reset: c's entry leaves the board; its rank face answers
        // found=false afterwards.
        using (var reset = await PostAsync(client, baseUrl + "/reset-leaderboard-entry", "alice-token",
            $$"""
            {"requestId":"op-web14-4","operator":"gm-alice","reason":"cheat wipe",
             {{key}},"subjectId":"c","expectedVersion":"{{adjustedVersion}}"}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            using var reply = JsonDocument.Parse(await reset.Content.ReadAsStringAsync());
            Assert.False(reply.RootElement.GetProperty("duplicate").GetBoolean());
            Assert.True(reply.RootElement.GetProperty("readVersion").GetString()!.Length > 0);
        }
        using (var rank = await PostAsync(client, baseUrl + "/get-leaderboard-rank", "alice-token",
            """
            {"tenantId":"tenant-a","gameId":"game-a","board":"score-alltime","periodId":"",
             "scopeId":"","subject":0,"subjectId":"c"}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, rank.StatusCode);
            using var reply = JsonDocument.Parse(await rank.Content.ReadAsStringAsync());
            Assert.False(reply.RootElement.GetProperty("found").GetBoolean());
        }

        // (7) The rebuild: the projection is re-derived from the effect ledger. The GM
        // correction is a ledger row, so it survives; the direct-seeded entries were never
        // ledger evidence and rightly do not come back. The rebuild names no expected
        // version (the entries are what was in doubt) and answers the board's fresh
        // version — the rewrite moved it, and the next command names that.
        string rebuildVersion;
        using (var rebuild = await PostAsync(client, baseUrl + "/rebuild-leaderboard", "alice-token",
            $$"""
            {"requestId":"op-web14-5","operator":"gm-alice","reason":"projection drift",
             {{key}}}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, rebuild.StatusCode);
            using var reply = JsonDocument.Parse(await rebuild.Content.ReadAsStringAsync());
            Assert.Equal(1, reply.RootElement.GetProperty("entries").GetInt32());
            rebuildVersion = reply.RootElement.GetProperty("readVersion").GetString()!;
            Assert.True(rebuildVersion.Length > 0);
        }
        using (var rebuilt = await PostAsync(client, baseUrl + "/get-leaderboard-top", "alice-token",
            $$"""
            {{{key}},"maxEntries":10,"pageToken":""}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, rebuilt.StatusCode);
            using var reply = JsonDocument.Parse(await rebuilt.Content.ReadAsStringAsync());
            var entries = reply.RootElement.GetProperty("entries");
            Assert.Equal(1, entries.GetArrayLength());
            Assert.Equal("a", entries[0].GetProperty("subjectId").GetString());
            Assert.Equal(2, entries[0].GetProperty("score").GetInt64());
        }

        // (8) The whole-board reset: an empty subject id wipes the bucket; the top face
        // answers an empty board — and the wipe is itself a ledger row, so a rebuild
        // replays it and the board stays empty. Corrections are never silently lost, and
        // neither is a wipe.
        using (var wipe = await PostAsync(client, baseUrl + "/reset-leaderboard-entry", "alice-token",
            $$"""
            {"requestId":"op-web14-6","operator":"gm-alice","reason":"season wipe",
             {{key}},"subjectId":"","expectedVersion":"{{rebuildVersion}}"}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, wipe.StatusCode);
            using var reply = JsonDocument.Parse(await wipe.Content.ReadAsStringAsync());
            Assert.False(reply.RootElement.GetProperty("duplicate").GetBoolean());
        }
        using (var empty = await PostAsync(client, baseUrl + "/get-leaderboard-top", "alice-token",
            $$"""
            {{{key}},"maxEntries":10,"pageToken":""}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            using var reply = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Equal(0, reply.RootElement.GetProperty("entries").GetArrayLength());
        }
        using (var replay = await PostAsync(client, baseUrl + "/rebuild-leaderboard", "alice-token",
            $$"""
            {"requestId":"op-web14-7","operator":"gm-alice","reason":"post-wipe rebuild",
             {{key}}}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var reply = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(0, reply.RootElement.GetProperty("entries").GetInt32());
        }

        // (9) The floor and the audited triple: a viewer cannot command (403), a mismatched
        // operator name is refused (403), and an incomplete command is a visible bad request.
        using (var viewer = await PostAsync(client, baseUrl + "/adjust-leaderboard-entry", "watcher-token",
            $$"""
            {"requestId":"op-web14-7","operator":"watcher","reason":"should not pass",
             {{key}},"subjectId":"a","delta":1,"expectedVersion":""}
            """))
        {
            Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        }
        using (var mismatch = await PostAsync(client, baseUrl + "/adjust-leaderboard-entry", "alice-token",
            $$"""
            {"requestId":"op-web14-8","operator":"someone-else","reason":"wrong name",
             {{key}},"subjectId":"a","delta":1,"expectedVersion":""}
            """))
        {
            Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        }
        using (var incomplete = await PostAsync(client, baseUrl + "/adjust-leaderboard-entry", "alice-token",
            $$"""
            {"requestId":"","operator":"gm-alice","reason":"",
             {{key}},"subjectId":"a","delta":1,"expectedVersion":""}
            """))
        {
            Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
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

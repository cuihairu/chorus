using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using System.Globalization;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The back-office leaderboard query face (FIX-WEB-12) end to end over the browser port:
/// the RANK-04/05/06 query answers — a bounded TopN page in board order with its read
/// version, the RANK-06 snapshot walk (a token continues the same snapshot, a stale or
/// changed board is an explicit error, never a cross-version page), the RANK-05 rank and
/// around window — all reachable as JSON with the operator bearer alone, on the viewer
/// floor. The board is seeded straight into the projection; the write-side descriptors
/// (aggregation, bounds, period calendar) are never named by the operator.
/// </summary>
public sealed class RedesignWeb12LeaderboardApiHostTests
{
    [Fact]
    public async Task TheBrowserFaceQueriesBoardsPagesRanksAndWindows()
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
        const string board = """
            {"tenantId":"tenant-a","gameId":"game-a","board":"score-alltime",
             "periodId":"","scopeId":"","subject":0
            """;

        // (1) The first page of the walk: board order (score desc, subject asc), bounded,
        // with the read version and the continuation token. The empty period and scope ids
        // name the all-time global bucket — the defaults, spelled on the wire.
        string firstToken;
        long firstVersion;
        using (var page = await PostAsync(client, baseUrl + "/get-leaderboard-top", "alice-token",
            $$"""
            {{board}},"maxEntries":2,"pageToken":""}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var reply = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
            var entries = reply.RootElement.GetProperty("entries");
            Assert.Equal(2, entries.GetArrayLength());
            Assert.Equal("a", entries[0].GetProperty("subjectId").GetString());
            Assert.Equal(5, entries[0].GetProperty("score").GetInt64());
            Assert.Equal("c", entries[1].GetProperty("subjectId").GetString());
            Assert.True(reply.RootElement.GetProperty("hasMore").GetBoolean());
            firstToken = reply.RootElement.GetProperty("nextPageToken").GetString()!;
            Assert.True(firstToken.Length > 0);
            firstVersion = reply.RootElement.GetProperty("readVersionUnixMs").GetInt64();
            Assert.True(firstVersion > 0);
        }

        // (2) The continuation: the token resumes strictly after the last entry, no
        // overlap, and the walk's snapshot version is unchanged.
        using (var page = await PostAsync(client, baseUrl + "/get-leaderboard-top", "alice-token",
            $$"""
            {{board}},"maxEntries":2,"pageToken":"{{firstToken}}"}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var reply = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
            var entries = reply.RootElement.GetProperty("entries");
            Assert.Equal(1, entries.GetArrayLength());
            Assert.Equal("b", entries[0].GetProperty("subjectId").GetString());
            Assert.False(reply.RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Equal("", reply.RootElement.GetProperty("nextPageToken").GetString());
            Assert.Equal(firstVersion, reply.RootElement.GetProperty("readVersionUnixMs").GetInt64());
        }

        // (3) The rank query: b's unique ordinal is 3 (ties take consecutive ranks);
        // an unknown subject answers found=false, never a folded answer.
        using (var rank = await PostAsync(client, baseUrl + "/get-leaderboard-rank", "alice-token",
            $$"""
            {"tenantId":"tenant-a","gameId":"game-a","board":"score-alltime","periodId":"",
             "scopeId":"","subject":0,"subjectId":"b"}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, rank.StatusCode);
            using var reply = JsonDocument.Parse(await rank.Content.ReadAsStringAsync());
            Assert.True(reply.RootElement.GetProperty("found").GetBoolean());
            Assert.Equal(3, reply.RootElement.GetProperty("rank").GetInt64());
            Assert.Equal(3, reply.RootElement.GetProperty("score").GetInt64());
            Assert.Equal(firstVersion, reply.RootElement.GetProperty("readVersionUnixMs").GetInt64());
        }
        using (var rank = await PostAsync(client, baseUrl + "/get-leaderboard-rank", "alice-token",
            """
            {"tenantId":"tenant-a","gameId":"game-a","board":"score-alltime","periodId":"",
             "scopeId":"","subject":0,"subjectId":"z-never-scored"}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, rank.StatusCode);
            using var reply = JsonDocument.Parse(await rank.Content.ReadAsStringAsync());
            Assert.False(reply.RootElement.GetProperty("found").GetBoolean());
        }

        // (4) The around window: b at rank 3 with window 1 answers ranks 2..3 in rank
        // order, at one read view.
        using (var around = await PostAsync(client, baseUrl + "/get-leaderboard-around", "alice-token",
            """
            {"tenantId":"tenant-a","gameId":"game-a","board":"score-alltime","periodId":"",
             "scopeId":"","subject":0,"subjectId":"b","window":1}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, around.StatusCode);
            using var reply = JsonDocument.Parse(await around.Content.ReadAsStringAsync());
            Assert.True(reply.RootElement.GetProperty("found").GetBoolean());
            var entries = reply.RootElement.GetProperty("entries");
            Assert.Equal(2, entries.GetArrayLength());
            Assert.Equal(2, entries[0].GetProperty("rank").GetInt64());
            Assert.Equal("c", entries[0].GetProperty("subjectId").GetString());
            Assert.Equal(3, entries[1].GetProperty("rank").GetInt64());
            Assert.Equal("b", entries[1].GetProperty("subjectId").GetString());
        }

        // (5) The walk is honest at its edges: a continuation after the board changed is
        // an explicit precondition error (restart the read, never a cross-version page),
        // a malformed token is refused, and a fresh walk answers the grown board.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var grow = new NpgsqlCommand("""
                INSERT INTO game.leaderboard_entry(tenant_id,game_id,board_id,period_id,scope_id,
                    subject_type,subject_id,score)
                VALUES ('tenant-a','game-a','score-alltime','alltime','global',0,'d',1);
                """, connection);
            await grow.ExecuteNonQueryAsync();
        }
        using (var stale = await PostAsync(client, baseUrl + "/get-leaderboard-top", "alice-token",
            $$"""
            {{board}},"maxEntries":2,"pageToken":"{{firstToken}}"}
            """))
        {
            // The transcoding maps the precondition failure (code 9) to an HTTP 400; the
            // body message separates the stale walk from the malformed token's 400.
            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.Contains("restart the read", await stale.Content.ReadAsStringAsync());
        }
        using (var malformed = await PostAsync(client, baseUrl + "/get-leaderboard-top", "alice-token",
            $$"""
            {{board}},"maxEntries":2,"pageToken":"not-a-cursor"}
            """))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
        using (var fresh = await PostAsync(client, baseUrl + "/get-leaderboard-top", "alice-token",
            $$"""
            {{board}},"maxEntries":10,"pageToken":""}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            using var reply = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
            Assert.Equal(4, reply.RootElement.GetProperty("entries").GetArrayLength());
            Assert.False(reply.RootElement.GetProperty("hasMore").GetBoolean());
        }

        // (6) The floor: a viewer reads, a credential-less caller is refused, and an
        // incomplete request is a visible bad request.
        using (var viewer = await PostAsync(client, baseUrl + "/get-leaderboard-top", "watcher-token",
            $$"""
            {{board}},"maxEntries":2,"pageToken":""}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        }
        using (var anonymous = await PostAsync(client, baseUrl + "/get-leaderboard-top", "",
            $$"""
            {{board}},"maxEntries":2,"pageToken":""}
            """))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        using (var incomplete = await PostAsync(client, baseUrl + "/get-leaderboard-top", "alice-token",
            """
            {"tenantId":"tenant-a","gameId":"","board":"","periodId":"","scopeId":"",
             "subject":0,"maxEntries":2,"pageToken":""}
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

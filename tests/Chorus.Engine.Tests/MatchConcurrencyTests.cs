using System.Net;
using System.Net.Http.Json;
using System.Text;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;
using Chorus.Gateway.Core.Faces;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review stage-3 acceptance (Match 并发): events for the same match from
/// several sessions POSTed concurrently are all conserved in the match
/// aggregate (no lost updates). Ordering across sessions is explicitly NOT
/// guaranteed (spec §2.3 - display aggregate, not an authority).
/// </summary>
public sealed class MatchConcurrencyTests
{
    public sealed class AllowAllFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
        }
    }

    [Fact]
    public async Task ConcurrentEvents_ConserveTotalsAcrossSessions()
    {
        using var factory = new AllowAllFactory();
        var client = factory.CreateClient();

        const int eventsPerSession = 200;
        var posts = new List<Task<HttpResponseMessage>>();
        for (var s = 1; s <= 2; s++)
        {
            var sessionId = $"s-match-{s}";
            var faction = s == 1 ? "red" : "blue";
            for (var i = 0; i < eventsPerSession; i++)
            {
                posts.Add(PostAsync(client, sessionId, faction, i));
            }
        }

        var responses = await Task.WhenAll(posts);
        Assert.All(responses, r => Assert.True(
            r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"unexpected status {(int)r.StatusCode}"));

        var matches = await client.GetFromJsonAsync<List<MatchDiagnostics>>("/matches");
        var match = Assert.Single(matches, m => m.Match.MatchId == "m-conc");

        Assert.Equal(2 * eventsPerSession, match.TotalEvents);
        Assert.Contains("s-match-1", match.Match.SessionIds);
        Assert.Contains("s-match-2", match.Match.SessionIds);

        var red = Assert.Single(match.Match.Factions, f => f.FactionId == "red");
        var blue = Assert.Single(match.Match.Factions, f => f.FactionId == "blue");
        Assert.Equal(new[] { "s-match-1" }, red.SessionIds);
        Assert.Equal(new[] { "s-match-2" }, blue.SessionIds);

        // TotalIngestedEvents advances when the actor drains its event lane,
        // not when the POST returns - poll for the eventual total instead of
        // reading it synchronously (raced the actor loop under full-suite load).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var ingested = 0L;
        while (DateTime.UtcNow < deadline)
        {
            var sessions = await client.GetFromJsonAsync<List<SessionDiagnostics>>("/sessions");
            ingested = sessions.Where(s => s.SessionId.StartsWith("s-match-")).Sum(s => s.TotalIngestedEvents);
            if (ingested >= 2 * eventsPerSession)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.Equal(2 * eventsPerSession, ingested);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string sessionId, string faction, int index)
    {
        var payload =
            $$"""{"sessionId":"{{sessionId}}","eventId":"e-{{sessionId}}-{{index}}","userId":"u{{index}}","kind":"comment","text":"hi","matchId":"m-conc","factionId":"{{faction}}"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingress/test")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        return await client.SendAsync(request);
    }
}

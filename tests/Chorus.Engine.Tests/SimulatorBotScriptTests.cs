using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Chorus.PlatformGateway;
using Chorus.SimulatorPlatform;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The bots platform's offline contracts: a script compiles to a fully known schedule
/// (pulses exact, steady streams within their segment windows, seeded runs reproducible),
/// a bad script names every defect at once, and the poster mints dedup-unique ids from one
/// shared sequence while counting every ingress outcome it is given.
/// </summary>
public sealed class SimulatorBotScriptTests
{
    private const string ValidScript = """
        [script]
        name = "bots-fact"
        duration = 5.0
        seed = 7

        [streamer]
        openId = "bots-open-1"
        liveKey = "bots-live-1"

        [[segment]]
        from = 0.0
        to = 5.0
        commentPerSecond = 2.0
        likePerSecond = 1.0
        text = "hello"

        [[pulse]]
        at = 1.0
        kind = "Gift"
        count = 3
        giftId = "g-1"
        giftValue = 40

        [[pulse]]
        at = 2.5
        kind = "comment"
        count = 5
        text = "burst"
        """;

    [Fact]
    public void AScriptCompilesAnExactScheduleAndTotalsBeforeAnythingIsSent()
    {
        var script = BotScript.Parse(ValidScript);

        Assert.Equal("bots-fact", script.Name);
        Assert.Equal("bots-open-1", script.OpenId);
        Assert.Equal("bots-live-1", script.LiveKey);

        // The schedule is sorted and the pulses are exact: three gifts at their exact
        // offsets, five burst comments one millisecond apart, whatever the steady streams
        // contributed always inside their segment window.
        Assert.Equal(script.Schedule,
            script.Schedule.OrderBy(evt => evt.OffsetSeconds).ToList());
        var gifts = script.Schedule.Where(evt => evt.Kind == ScriptEventKind.Gift).ToList();
        Assert.Equal(new[] { 1.0, 1.001, 1.002 },
            gifts.Select(evt => Math.Round(evt.OffsetSeconds, 6)));
        Assert.All(gifts, evt =>
        {
            Assert.Equal("g-1", evt.GiftId);
            Assert.Equal(40, evt.GiftValue);
            Assert.Matches("^bot-v\\d+$", evt.UserId);
        });
        var burst = script.Schedule
            .Where(evt => evt.Kind == ScriptEventKind.Comment && evt.Text == "burst").ToList();
        Assert.Equal(5, burst.Count);
        Assert.Equal(new[] { 2.5, 2.501, 2.502, 2.503, 2.504 },
            burst.Select(evt => Math.Round(evt.OffsetSeconds, 6)));

        // Steady streams: 2 comments/s and 1 like/s for five seconds with a jittered first
        // beat — the count wobbles by at most one around the rate × window, and every event
        // sits inside the segment. (The burst pulse also comments; the segment's carry the
        // segment text.)
        var segmentComments = script.Schedule
            .Count(evt => evt.Kind == ScriptEventKind.Comment && evt.Text == "hello");
        var likes = script.Schedule.Count(evt => evt.Kind == ScriptEventKind.Like);
        Assert.InRange(segmentComments, 9, 11);
        Assert.InRange(likes, 4, 6);
        Assert.All(script.Schedule, evt => Assert.InRange(evt.OffsetSeconds, 0, 5.0));
        Assert.DoesNotContain(script.Schedule, evt => evt.OffsetSeconds >= 5.0);

        // Totals are derived from the same schedule, so a run's word is known up front.
        Assert.Equal(3, script.Totals.ByKind[ScriptEventKind.Gift]);
        Assert.Equal((long)(segmentComments + burst.Count),
            script.Totals.ByKind[ScriptEventKind.Comment]);
        Assert.Equal((long)likes, script.Totals.ByKind[ScriptEventKind.Like]);
        Assert.Equal(120, script.Totals.GiftValueSum);
    }

    [Fact]
    public void ASeededScriptCompilesIdenticallyAndADifferentSeedDiffers()
    {
        var first = BotScript.Parse(ValidScript);
        var again = BotScript.Parse(ValidScript);
        Assert.Equal(
            first.Schedule.Select(evt => (evt.OffsetSeconds, evt.Kind, evt.UserId)),
            again.Schedule.Select(evt => (evt.OffsetSeconds, evt.Kind, evt.UserId)));
        // Totals hold dictionaries — compared by content, not reference.
        Assert.Equal(first.Totals.ByKind, again.Totals.ByKind);
        Assert.Equal(first.Totals.GiftValueSum, again.Totals.GiftValueSum);

        var otherSeed = BotScript.Parse(ValidScript.Replace("seed = 7", "seed = 8"));
        Assert.NotEqual(
            first.Schedule.Select(evt => evt.OffsetSeconds),
            otherSeed.Schedule.Select(evt => evt.OffsetSeconds));
    }

    [Fact]
    public void ABadScriptNamesAllItsDefectsAtOnce()
    {
        var bad = """
            [script]
            name = "broken"
            duration = 5.0

            [[segment]]
            from = 4.0
            to = 2.0
            giftPerSecond = 1.0

            [[pulse]]
            at = 1.0
            kind = "Nope"
            count = 2

            [[pulse]]
            at = 6.0
            kind = "Gift"
            count = 1
            """;

        IReadOnlyList<string> errors = [];
        var thrown = Assert.Throws<ArgumentException>(() => BotScript.Parse(bad, 8, out errors));
        Assert.Equal(6, errors.Count);
        Assert.Contains(errors, defect => defect.Contains("missing [streamer] table"));
        Assert.Contains(errors, defect => defect.Contains("must be before to"));
        Assert.Contains(errors, defect => defect.Contains("a gift rate needs giftId"));
        Assert.Contains(errors, defect => defect.Contains("is not a script event kind"));
        Assert.Contains(errors, defect => defect.Contains("at or past the script duration"));
        Assert.Contains(errors, defect => defect.Contains("a Gift pulse needs giftId and giftValue"));
        Assert.Contains("Invalid bot script:", thrown.Message);
    }

    [Fact]
    public void ANegativeSteadyRateIsADefect()
    {
        var negative = """
            [script]
            duration = 5.0

            [streamer]
            openId = "bots-open-1"
            liveKey = "bots-live-1"

            [[segment]]
            from = 0.0
            to = 5.0
            commentPerSecond = -1.0
            """;

        IReadOnlyList<string> errors = [];
        Assert.Throws<ArgumentException>(() => BotScript.Parse(negative, 8, out errors));
        Assert.Contains(errors, defect =>
            defect.Contains("rate must be a finite non-negative number"));
    }

    [Fact]
    public void AGibberishDocumentIsADefectNotACrash()
    {
        IReadOnlyList<string> errors = [];
        Assert.Throws<ArgumentException>(() =>
            BotScript.Parse("not a toml document [[[", 8, out errors));
        Assert.Contains(errors, defect => defect.StartsWith("toml: "));
    }

    [Fact]
    public void ThePosterNamesTheGatewayAppIdHeader() =>
        Assert.Equal(PlatformIngressEndpoint.AppIdHeader, AudienceEventPoster.AppIdHeader);

    [Fact]
    public async Task PostersShareOneSequenceCountEveryOutcomeAndSendTheMapperShape()
    {
        var port = RedesignM0HostTests.HostEnvironment.FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/ingress/");
        listener.Start();
        var bodies = new ConcurrentQueue<string>();
        var answers = new ConcurrentQueue<(int Status, string Body)>(
        [
            (200, """{"receipts":[{"eventId":"a-bot-1","disposition":"DurableAccepted","duplicated":false}]}"""),
            (200, """{"receipts":[{"eventId":"a-bot-2","disposition":"Duplicate","duplicated":true}]}"""),
            (401, """{"error":"unauthorized"}"""),
            (429, """{"error":"rate_limited"}"""),
        ]);
        var serving = Task.Run(async () =>
        {
            try
            {
                while (answers.TryDequeue(out var answer))
                {
                    var context = await listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream);
                    bodies.Enqueue(await reader.ReadToEndAsync());
                    context.Response.StatusCode = answer.Status;
                    var payload = Encoding.UTF8.GetBytes(answer.Body);
                    await context.Response.OutputStream.WriteAsync(payload);
                    context.Response.Close();
                }
            }
            catch
            {
                // the listener stops when the fact is done; a mid-wait stop surfaces here
            }
        });

        try
        {
            using var http = new HttpClient();
            var sequence = 0L;
            Func<long> shared = () => Interlocked.Increment(ref sequence);
            var posterA = new AudienceEventPoster(http, $"http://127.0.0.1:{port}",
                "bots-live-1", "a-", shared);
            var posterB = new AudienceEventPoster(http, $"http://127.0.0.1:{port}",
                "bots-live-2", "b-", shared);

            var accepted = await posterA.PostAsync(
                new ScheduledBotEvent(0, ScriptEventKind.Gift, "bot-v0", null, "g-1", 40));
            var duplicated = await posterA.PostAsync(
                new ScheduledBotEvent(0, ScriptEventKind.Comment, "bot-v1", "hi", null, 0));
            var rejected = await posterB.PostAsync(
                new ScheduledBotEvent(0, ScriptEventKind.Gift, "bot-v0", null, "g-1", 40));
            var throttled = await posterB.PostAsync(
                new ScheduledBotEvent(0, ScriptEventKind.Like, "bot-v2", null, null, 0));

            // Receipts answer per event; the ids draw from the one shared sequence.
            Assert.Equal("DurableAccepted", accepted.Disposition);
            Assert.False(accepted.Duplicated);
            Assert.Equal("Duplicate", duplicated.Disposition);
            Assert.True(duplicated.Duplicated);
            Assert.Null(rejected.Disposition);
            Assert.Null(throttled.Disposition);
            Assert.Equal(4, sequence);

            var snapshotA = posterA.Snapshot();
            var snapshotB = posterB.Snapshot();
            Assert.Equal(1, snapshotA.SentByKind[ScriptEventKind.Gift]);
            Assert.Equal(1, snapshotA.SentByKind[ScriptEventKind.Comment]);
            Assert.Equal(1, snapshotA.Accepted);
            Assert.Equal(1, snapshotA.Duplicated);
            Assert.Equal(1, snapshotB.SentByKind[ScriptEventKind.Gift]);
            Assert.Equal(1, snapshotB.SentByKind[ScriptEventKind.Like]);
            Assert.Equal(1, snapshotB.Unauthorized);
            Assert.Equal(1, snapshotB.RateLimited);
            Assert.Equal(0, snapshotB.Other);

            // The wire shape is the simulator mapper's, and no two events share an id.
            var sent = bodies.ToList();
            Assert.Equal(4, sent.Count);
            var ids = sent.Select(body =>
                JsonDocument.Parse(body).RootElement.GetProperty("eventId").GetString()!).ToList();
            Assert.Equal(4, ids.Distinct().Count());
            Assert.All(ids, id => Assert.Matches("^[ab]-bot-[1-4]$", id));
            Assert.Contains(sent, body => body.Contains(
                "\"sessionId\":\"bots-live-1\",\"eventId\":\"a-bot-1\",\"kind\":\"Gift\"," +
                "\"userId\":\"bot-v0\",\"giftId\":\"g-1\",\"giftCount\":1,\"giftValue\":40"));
        }
        finally
        {
            listener.Stop();
        }
        await serving;
    }
}

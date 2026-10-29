using Chorus.DataServer;
using Chorus.Protocol.Redesign;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-READ-05: the recovery plan travels as bounded pages, never as one grown message.
/// The pager's contract is assembly fidelity — every page of one view reassembles the
/// whole plan with no duplicate and no gap — plus explicit limits: a page carries at
/// most RowLimit rows or ByteLimit payload bytes, whichever fills first, and always at
/// least one row so the read advances. A cursor replays its page identically (retry
/// safety), the last page carries no token, and nothing but the token is held between
/// requests: cancelling is simply not asking again.
/// </summary>
public sealed class RedesignRecoveryPagingUnitTests
{
    private readonly ITestOutputHelper _output;

    public RedesignRecoveryPagingUnitTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AssemblingEveryPageReproducesTheWholePlanWithoutDuplicatesOrGaps()
    {
        var plan = BigPlan();
        var cursor = new RecoveryPageToken(2, plan.Epoch, plan.LastTick, 0, 0);

        var snapshots = new List<RecoverySnapshot>();
        var frames = new List<RecoveryFrame>();
        var deliveries = new List<RecoveryDelivery>();
        var participants = new List<RoundParticipantState>();
        var pages = 0;
        while (cursor is not null)
        {
            var (page, next) = RecoveryPager.TakePage(plan, cursor);
            Assert.Equal(plan.RoomId, page.RoomId);
            Assert.Equal(plan.Status, page.Status);
            Assert.Equal(plan.Epoch, page.Epoch);
            Assert.Equal(plan.LastTick, page.LastTick);
            Assert.Equal(plan.StateVersion, page.StateVersion);
            var rows = page.Snapshots.Count + page.Frames.Count + page.Deliveries.Count
                + page.Participants.Count;
            Assert.InRange(rows, 1, RecoveryPager.RowLimit);
            var bytes = page.Snapshots.Sum(snapshot =>
                    snapshot.Payload.LongLength + snapshot.PayloadHash.LongLength)
                + page.Frames.Sum(frame => frame.StateHash.LongLength)
                + page.Deliveries.Sum(delivery => (long)delivery.PayloadJson.Length);
            // The byte cap holds for every page except the one-row page a single huge
            // snapshot forces: the read must always advance past such a row.
            if (rows > 1) Assert.True(bytes <= RecoveryPager.ByteLimit,
                $"a {rows}-row page carried {bytes} bytes");
            snapshots.AddRange(page.Snapshots);
            frames.AddRange(page.Frames);
            deliveries.AddRange(page.Deliveries);
            participants.AddRange(page.Participants);
            cursor = next;
            pages++;
            Assert.True(pages < 100, "the pager did not reach the end of the plan");
        }

        Assert.True(pages > 1, "the plan did not actually need paging");
        _output.WriteLine($"{pages} pages for {frames.Count} assembled rows");
        // Assembly fidelity: the same material, in the same order, exactly once.
        Assert.Equal(plan.Snapshots, snapshots);
        Assert.Equal(plan.Frames, frames);
        Assert.Equal(plan.Deliveries, deliveries);
        Assert.Equal(plan.Participants, participants);
    }

    [Fact]
    public void ACursorReplaysItsPageAndTheLastPageCarriesNoToken()
    {
        var plan = BigPlan();
        var start = new RecoveryPageToken(4, plan.Epoch, plan.LastTick, 0, 0);
        var (first, next) = RecoveryPager.TakePage(plan, start);
        Assert.NotNull(next);
        // The same cursor answers the same page — a retried request replays, never
        // skips or double-delivers.
        var (retry, retryNext) = RecoveryPager.TakePage(plan, start);
        Assert.Equal(first.Frames, retry.Frames);
        Assert.Equal(first.Snapshots, retry.Snapshots);
        Assert.Equal(next, retryNext);

        // Walking to the end: the page that exhausts the material carries no cursor.
        RecoveryPageToken? cursor = start;
        RecoveryPageToken? last = null;
        var seen = 0;
        while (cursor is not null)
        {
            (_, var following) = RecoveryPager.TakePage(plan, cursor);
            last = following;
            cursor = following;
            seen++;
        }
        Assert.Null(last);
        Assert.True(seen > 1);

        // Tokens survive the wire round trip and refuse foreign bytes.
        var token = new RecoveryPageToken(8, 4242, 900, 2, 17);
        Assert.Equal(token, RecoveryPager.Decode(RecoveryPager.Encode(token)));
        Assert.Null(RecoveryPager.Decode("not-a-token"u8));
        Assert.Null(RecoveryPager.Decode("""{"depth":2}"""u8));
    }

    [Fact]
    public void APlanWithoutMaterialAnswersOneEmptyPage()
    {
        var plan = new RecoveryPlan("room-p3", "Running", 3, 0, 1,
            [], [], [], [], null);
        var (page, next) = RecoveryPager.TakePage(plan,
            new RecoveryPageToken(2, 3, 0, 0, 0));
        Assert.Empty(page.Snapshots);
        Assert.Empty(page.Frames);
        Assert.Empty(page.Deliveries);
        Assert.Empty(page.Participants);
        Assert.Null(next);
    }

    /// <summary>A plan with every page shape: small snapshots, one huge snapshot larger
    /// than the byte cap, more frames than the row cap, two pages of deliveries and a
    /// participant tail.</summary>
    private static RecoveryPlan BigPlan()
    {
        var snapshots = new List<RecoverySnapshot>
        {
            new(1200, 1, new byte[64], new byte[32], true),
            new(900, 1, new byte[1024 * 1024 + 512 * 1024], new byte[32], true),
            new(600, 1, new byte[64], new byte[32], true),
            new(300, 1, new byte[64], new byte[32], true)
        };
        var frames = Enumerable.Range(1, 600).Select(tick => new RecoveryFrame(
            tick, tick % 3 == 0 ? [$"event-{tick}"] : [], new byte[32], "1.0.0", "cfg-1")).ToList();
        var deliveries = Enumerable.Range(1, 500).Select(sequence => new RecoveryDelivery(
            $"event-{sequence}", sequence, $"binding-{sequence % 7}",
            $$"""{"kind":"gift","amount":{{sequence}}}""", sequence % 2 == 0)).ToList();
        var participants = Enumerable.Range(0, 3).Select(index => new RoundParticipantState(
            $"participant-{index}", $"binding-{index}", index == 0 ? "Host" : "Guest", "Active")).ToList();
        return new RecoveryPlan("room-p3", "Running", 7, 1200, 1201,
            snapshots, deliveries, frames, participants, null);
    }
}

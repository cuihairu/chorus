using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// Acceptance for the latest-wins slot's observability (audit B3 /
/// frame-sync-best-practices §可观测性, "待导出"): a replacement that
/// evicts a queued snapshot must be counted on <see cref="OutboundClientQueue.Dropped"/>.
/// The counter used to be dead code - the slot is capacity 1 with
/// <c>BoundedChannelFullMode.DropOldest</c>, so <c>TryWrite</c> returns true
/// while silently evicting the older item and never failed; only a completed
/// writer returned false. Real drops were therefore invisible.
/// </summary>
public sealed class OutboundClientQueueTests
{
    [Fact]
    public async Task DropOldestEviction_IsCountedOnDropped()
    {
        var client = new BlockingClient();
        await using var queue = new OutboundClientQueue(client);

        await queue.SendSnapshotAsync(Wire(1), CancellationToken.None);

        // The pump has picked up wire 1 and is stuck sending it, so the slot
        // is free: wire 2 lands in it, wire 3 and wire 4 have nowhere to go.
        await client.Entered.Task;
        await queue.SendSnapshotAsync(Wire(2), CancellationToken.None);
        await queue.SendSnapshotAsync(Wire(3), CancellationToken.None);
        await queue.SendSnapshotAsync(Wire(4), CancellationToken.None);

        // wire 3 evicts wire 2, wire 4 evicts wire 3: exactly two replacements,
        // each counted. The old counter saw none of them.
        Assert.Equal(2, queue.Dropped);
    }

    private static WireSnapshot Wire(long tickId) => new(
        new SessionSnapshot("s-queue", tickId, 0, new { }),
        "[]u="u8.ToArray());

    private sealed class BlockingClient : ISessionClient
    {
        public string ClientId => "c-block";

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return new ValueTask(Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }
}

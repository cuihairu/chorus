using System.Text.Json;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Control;

public sealed class ControlTickDurability(ControlClient client, RoomRecord room, int maxEvents = 200) : ISessionTickDurability
{
    public async Task<IReadOnlyList<AudienceEvent>> PrepareAsync(CancellationToken cancellationToken)
    {
        var result = await client.Post<RoomRecord>($"rooms/{Uri.EscapeDataString(room.Id)}/prepare", new OwnerRequest(client.NodeId, client.InstanceId, room.Epoch), cancellationToken);
        return (result.Inbox ?? []).Take(Math.Max(1,maxEvents)).ToArray();
    }
    public async Task CommitAsync(long expectedTick, long tick, object state, IReadOnlyList<AudienceEvent> consumed, CancellationToken cancellationToken)
    {
        await client.Send($"rooms/{Uri.EscapeDataString(room.Id)}/commit", new TickCommit(client.NodeId,client.InstanceId,room.Epoch,
            expectedTick,tick,1,JsonSerializer.SerializeToElement(state,ControlDatabase.Json),consumed.Select(RoomControlStore.EventKey).ToArray()), cancellationToken);
    }
}

using System.Text.Json;
using Chorus.Control;
using Chorus.Data;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Routing;
using Chorus.EngineHost;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

public sealed class ControlCheckpointRestorerTests
{
    [Fact]
    public void ValidCheckpointRestoresModuleStateAndSchemaVersion()
    {
        using var module = new RecordingModule();
        var state = JsonSerializer.SerializeToElement(new { score = 42 });
        var room = Checkpoint(state);

        ControlCheckpointRestorer.Restore(module, room);

        Assert.Equal(1,module.RestoredVersion);
        Assert.Equal(42,module.RestoredState!.Value.GetProperty("score").GetInt32());
    }

    [Theory]
    [InlineData(2, 123)]
    [InlineData(1, 0)]
    public void IncompatibleOrCorruptCheckpointFailsBeforeMutatingModule(int schema, long checksum)
    {
        using var module = new RecordingModule();
        var room = Checkpoint(JsonSerializer.SerializeToElement(new { score = 42 })) with
        { SchemaVersion = schema, Checksum = checksum };

        Assert.Throws<InvalidOperationException>(()=>ControlCheckpointRestorer.Restore(module,room));
        Assert.Equal(0,module.RestoredVersion);
        Assert.Null(module.RestoredState);
    }

    private static RoomRecord Checkpoint(JsonElement state) => new("room","worker","boot",3,
        RoomLifecycleState.Recovering,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),TickId:8,
        Snapshot:state,Checksum:SnapshotIntegrity.Compute(state.GetRawText()),SchemaVersion:1);

    private sealed class RecordingModule : IGameModule
    {
        public string Name => "checkpoint-test";
        public int RestoredVersion { get; private set; }
        public JsonElement? RestoredState { get; private set; }
        public object GetStateSnapshot() => new { };
        public void ApplyEvents(long tickId,IReadOnlyList<AudienceEvent> events) { }
        public void RestoreState(long version,JsonElement state) { RestoredVersion=(int)version;RestoredState=state.Clone(); }
        public void Dispose() { }
    }
}

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Gateway.Core.Actors;
using Chorus.Protocol;
using Chorus.Protocol.Models;
using Chorus.SampleGame;
using Chorus.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// 模块快照恢复金测（todo §10 恢复半边 + §18 事件补齐门，契约 =
/// docs/design/snapshot-restore.md 方案 A，2026-10-14 拍板）：
/// <list type="bullet">
/// <item>恢复 = 会话出生点（actor 工厂内、actor 存在前）唯一一次回写；恢复完成前
/// tick 与 ingress 都碰不到模块（不存在未恢复的 actor）。</item>
/// <item>恢复输入 = 线协议 JSON（GetStateSnapshot 往返）；未知版本/超体积/模块异常
/// 一律 fail-closed：会话拒绝启动，注册表计入 StartFailures，不留半状态 actor。</item>
/// <item>单体与拆分两处出生点走同一个单点 restorer（parity e2e）。</item>
/// </list>
/// </summary>
public sealed class ModuleRestoreTests
{
    private static SessionConfig Config(int maxSnapshotBytes = 0) => new()
    {
        TickDurationMs = 10,
        SnapshotEveryTicks = 0,
        MaxSnapshotBytes = maxSnapshotBytes,
    };

    private static AudienceEvent CommentEvent(string sessionId, string eventId, string userId = "u1") => new(
        EventId: eventId,
        Platform: "douyin",
        SessionId: sessionId,
        UserId: userId,
        Kind: AudienceEventKind.Comment,
        IngestTimeMs: 0);

    // ---- 假体 ----

    private sealed class RecordingModule : IGameModule
    {
        private readonly object _gate = new();
        private readonly List<string> _calls = new();

        public bool RejectUnknownVersion;
        public bool ThrowOnRestore;
        public bool BlockInRestore;
        public readonly ManualResetEventSlim RestoreEntered = new();
        public readonly ManualResetEventSlim RestoreRelease = new();
        public int RestoreCalls;

        public string Name => "recording";

        public object GetStateSnapshot() => new { calls = Calls.Count };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events) => Record($"apply:{tickId}");

        public void RestoreState(long version, JsonElement state)
        {
            Interlocked.Increment(ref RestoreCalls);
            Record("restore");
            if (RejectUnknownVersion && version != 1)
            {
                throw new InvalidOperationException($"unsupported version {version}");
            }

            if (ThrowOnRestore)
            {
                throw new InvalidOperationException("boom");
            }

            if (BlockInRestore)
            {
                RestoreEntered.Set();
                RestoreRelease.Wait(TimeSpan.FromSeconds(5));
            }
        }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        private void Record(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
        }

        public void Dispose() { }
    }

    private sealed class FixedModuleFactory : IGameModuleFactory
    {
        private readonly IGameModule _module;
        public FixedModuleFactory(IGameModule module) => _module = module;
        public IGameModule Create(string sessionId) => _module;
    }

    private sealed class MemoryRecoverySource : ISnapshotRecoverySource
    {
        private readonly Dictionary<string, SnapshotRecovery> _snapshots = new(StringComparer.Ordinal);

        public MemoryRecoverySource(string sessionId, long version, object state, bool removeAfterRead = false)
        {
            _snapshots[sessionId] = new SnapshotRecovery(
                version,
                JsonSerializer.SerializeToElement(state, ProtocolJson.SerializerOptions));
            RemoveAfterRead = removeAfterRead;
        }

        private bool RemoveAfterRead;

        public bool TryGet(string sessionId, out SnapshotRecovery recovery)
        {
            recovery = default!;
            if (!_snapshots.TryGetValue(sessionId, out var found))
            {
                return false;
            }

            recovery = found;
            if (RemoveAfterRead)
            {
                _snapshots.Remove(sessionId);
            }

            return true;
        }
    }

    // ---- §7.1 恢复后首个快照与权威快照语义一致（模块级往返） ----

    [Fact]
    public void SampleModule_Restore_RoundTripsSessionOwnedState()
    {
        var original = new SampleGameModule("s-rt");
        original.ApplyEvents(10, new[]
        {
            new AudienceEvent("e1", "douyin", "s-rt", "u1", AudienceEventKind.Like, 0, Text: "3",
                MatchId: "m-1", FactionId: "f-1"),
            CommentEvent("s-rt", "e2", "u2"),
            new AudienceEvent("e3", "douyin", "s-rt", "u3", AudienceEventKind.Gift, 0, GiftCount: 2),
        });

        // 权威快照 = GetStateSnapshot 的线协议 JSON（恢复源携带的形态）。
        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(original.GetStateSnapshot(), ProtocolJson.SerializerOptions);
        using var doc = JsonDocument.Parse(snapshotBytes);

        var restored = new SampleGameModule("s-rt");
        restored.RestoreState(version: 1, doc.RootElement);

        // match 节是跨会话 store 投影，不属模块私有恢复契约（恢复后随新事件重建）。
        Assert.Equal(WithoutMatch(original.GetStateSnapshot()), WithoutMatch(restored.GetStateSnapshot()));

        // 恢复出的状态是活的：新事件从恢复点继续累积，matchId 也已恢复。
        restored.ApplyEvents(11, new[] { CommentEvent("s-rt", "e4", "u2") });
        var after = JsonSerializer.SerializeToElement(restored.GetStateSnapshot(), ProtocolJson.SerializerOptions);
        Assert.Equal(11, after.GetProperty("tickId").GetInt64());
        Assert.Equal("m-1", after.GetProperty("matchId").GetString());
        Assert.Equal(2, after.GetProperty("totals").GetProperty("comments").GetInt64());
    }

    [Fact]
    public void SampleModule_UnknownVersion_Throws()
    {
        using var doc = JsonDocument.Parse("{}");
        var module = new SampleGameModule("s-uv");

        Assert.Throws<InvalidOperationException>(() => module.RestoreState(version: 2, doc.RootElement));
    }

    [Fact]
    public void SampleModule_MalformedState_Throws()
    {
        using var doc = JsonDocument.Parse("{\"tickId\":7}");
        var module = new SampleGameModule("s-bad");

        Assert.Throws<InvalidOperationException>(() => module.RestoreState(version: 1, doc.RootElement));
    }

    [Fact]
    public void Restorer_VersionBelowOne_FailsVisible_NeverTouchesModule()
    {
        // The restorer's own version gate (below the module's say): version 0
        // must fail closed with the session id on the exception, and the
        // module's RestoreState must never run.
        var module = new RecordingModule();
        var source = new MemoryRecoverySource("s-ver", 0, new { calls = 0 });

        var ex = Assert.Throws<SnapshotRestoreException>(
            () => GameModuleRestorer.Restore(module, "s-ver", source, Config()));

        Assert.Equal("s-ver", ex.SessionId);
        Assert.Equal(0, module.RestoreCalls);
    }

    // ---- §7.2 恢复完成前 ApplyEvents 不被调用 ----

    [Fact]
    public async Task Restore_CompletesBeforeAnyEvents_NoSecondRestoreUnderConcurrency()
    {
        var module = new RecordingModule { BlockInRestore = true };
        var source = new MemoryRecoverySource("s-race", 1, new { calls = 0 });
        await using var sessions = new SessionManager(new FixedModuleFactory(module), Config(), source);
        var router = new InProcSessionActorRouter(sessions);

        // 恢复进行中：actor 尚不存在，并发的第二次出生与事件投递都不可能到达模块。
        var first = Task.Run(() => sessions.GetOrCreate("s-race"));
        Assert.True(module.RestoreEntered.Wait(TimeSpan.FromSeconds(5)), "restore never started");
        var second = Task.Run(() => sessions.GetOrCreate("s-race"));
        await Task.Delay(100);
        Assert.False(second.IsCompleted, "a second birth completed while restore was in flight");

        module.RestoreRelease.Set();
        var a1 = await first;
        var a2 = await second;
        Assert.Same(a1, a2);
        Assert.Equal(1, module.RestoreCalls);

        // 恢复之后事件才开始流入，且严格晚于恢复。
        var outcome = await router.Resolve("s-race").TellAsync(CommentEvent("s-race", "e1"), CancellationToken.None);
        Assert.Equal(SessionTellOutcome.Accepted, outcome);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !module.Calls.Any(c => c.StartsWith("apply:")))
        {
            await Task.Delay(10);
        }

        Assert.Contains(module.Calls, c => c.StartsWith("apply:"));
        Assert.Equal("restore", module.Calls[0]);
    }

    // ---- 无快照 = 全新启动（今天的默认行为不变） ----

    [Fact]
    public async Task NoSource_FreshStart_ModuleNeverRestored()
    {
        var module = new RecordingModule();
        await using var sessions = new SessionManager(new FixedModuleFactory(module), Config());
        var router = new InProcSessionActorRouter(sessions);

        var outcome = await router.Resolve("s-fresh").TellAsync(CommentEvent("s-fresh", "e1"), CancellationToken.None);

        Assert.Equal(SessionTellOutcome.Accepted, outcome);
        Assert.Equal(0, sessions.StartFailures);
        Assert.Equal(0, module.RestoreCalls);
    }

    [Fact]
    public async Task SourceWithoutSnapshot_FreshStart_ModuleNeverRestored()
    {
        var module = new RecordingModule();
        var source = new MemoryRecoverySource("other-session", 1, new { calls = 0 });
        await using var sessions = new SessionManager(new FixedModuleFactory(module), Config(), source);

        sessions.GetOrCreate("s-other");

        Assert.Equal(0, module.RestoreCalls);
    }

    // ---- §7.3 未知版本 fail-closed + 可观测；重试可以全新启动 ----

    [Fact]
    public async Task Restore_UnknownVersion_FailsClosed_Counted_RetryStartsFresh()
    {
        var module = new RecordingModule { RejectUnknownVersion = true };
        var source = new MemoryRecoverySource("s-bad", 99, new { }, removeAfterRead: true);
        await using var sessions = new SessionManager(new FixedModuleFactory(module), Config(), source);

        Assert.Throws<SnapshotRestoreException>(() => sessions.GetOrCreate("s-bad"));
        Assert.Equal(1, sessions.StartFailures);
        Assert.Equal(0, sessions.Count);
        Assert.Empty(sessions.GetDiagnostics());

        // 快照源不再提供快照 → 同一会话可全新启动，不再二次恢复。
        var actor = sessions.GetOrCreate("s-bad");
        Assert.NotNull(actor);
        Assert.Equal(1, sessions.StartFailures);
        Assert.Equal(1, sessions.Count);
        Assert.Equal(1, module.RestoreCalls);
    }

    // ---- §7.4 超体积拒绝（复用 MaxSnapshotBytes 门，先于模块恢复） ----

    [Fact]
    public async Task Restore_OversizedState_Rejected_BeforeModuleRestore()
    {
        var module = new RecordingModule();
        var source = new MemoryRecoverySource("s-big", 1, new { blob = new string('x', 512) });
        await using var sessions = new SessionManager(new FixedModuleFactory(module), Config(maxSnapshotBytes: 64), source);

        var ex = Assert.Throws<SnapshotRestoreException>(() => sessions.GetOrCreate("s-big"));

        Assert.Contains("exceeds cap", ex.Message);
        Assert.Equal(0, module.RestoreCalls);
        Assert.Equal(1, sessions.StartFailures);
        Assert.Equal(0, sessions.Count);
    }

    // ---- §7.5 模块恢复异常 fail-closed，不留半状态 actor ----

    [Fact]
    public async Task Restore_ModuleThrows_FailsClosed_NoHalfStateActor()
    {
        var module = new RecordingModule { ThrowOnRestore = true };
        var source = new MemoryRecoverySource("s-throw", 1, new { calls = 0 });
        await using var sessions = new SessionManager(new FixedModuleFactory(module), Config(), source);

        var ex = Assert.Throws<SnapshotRestoreException>(() => sessions.GetOrCreate("s-throw"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("boom", ex.InnerException!.Message);
        Assert.Equal(0, sessions.Count);
        Assert.Empty(sessions.GetDiagnostics());
        Assert.Equal(1, sessions.StartFailures);
    }


    private static string WithoutMatch(object snapshot)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, ProtocolJson.SerializerOptions);
        using var doc = JsonDocument.Parse(bytes);
        var plain = new MemoryStream();
        using (var writer = new Utf8JsonWriter(plain))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name == "match")
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Serialize(JsonDocument.Parse(plain.ToArray()).RootElement);
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// 快照持久化金测（todo §15 持久化项，契约 = 审计「持久化快照必须有版本与完整性校验」）：
/// 存储后端可插拔（DB 选型按需求决定，不硬编码），快照版本单调守卫（旧快照不得覆盖新状态），
/// 校验和在存储侧重算（不信调用方自报），读取校验失败 fail-visible 抛错而非当作「未持久化」。
/// 状态重建（IGameModule 恢复契约）属 §10 待拍板项，不在本层。
/// </summary>
public sealed class StateSnapshotStoreTests
{
    private const string Payload = """{"tick":42,"players":{"p-1":10}}""";

    private static StateSnapshot NewSnapshot(
        string roomId = "room-1",
        long version = 1,
        int schemaVersion = 1,
        string payload = Payload,
        long checksum = 0L) =>
        new(roomId, version, schemaVersion, payload, checksum);

    private static SnapshotStore NewStore() => new(new InMemorySnapshotBackend());

    [Fact]
    public void Save_NewRoom_PersistsAndLoadReturnsIt()
    {
        var store = NewStore();

        var saved = store.Save(NewSnapshot());

        Assert.True(saved.Checksum != 0L);
        var loaded = store.Load("room-1");
        Assert.NotNull(loaded);
        Assert.Equal(Payload, loaded!.Payload);
        Assert.Equal(saved.Checksum, loaded.Checksum);
    }

    [Fact]
    public void Save_RecomputesChecksum_IgnoresCallerValue()
    {
        var store = NewStore();

        // 校验和在存储侧重算：调用方自报的校验和不可信，不能拿来自证。
        var saved = store.Save(NewSnapshot(checksum: 0xDEADBEEFL));

        Assert.Equal(SnapshotIntegrity.Compute(Payload), saved.Checksum);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData(" ", 1)]
    [InlineData("room-1", 0)]
    public void Save_BlankField_Throws(string roomId, long version)
    {
        var store = NewStore();

        Assert.ThrowsAny<ArgumentException>(() => store.Save(NewSnapshot(roomId, version)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Save_NonPositiveVersion_Throws(long version)
    {
        var store = NewStore();

        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => store.Save(NewSnapshot(version: version)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Save_NonPositiveSchemaVersion_Throws(int schemaVersion)
    {
        var store = NewStore();

        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => store.Save(NewSnapshot(schemaVersion: schemaVersion)));
    }

    [Fact]
    public void Save_StaleVersion_RejectedWithoutOverwrite()
    {
        var store = NewStore();
        store.Save(NewSnapshot(version: 5, payload: "newer"));

        // 旧快照不得覆盖新状态（旧写者隔离的存储半边）：false 且已存记录不变。
        Assert.False(store.TrySave(NewSnapshot(version: 5, payload: "older")));
        Assert.False(store.TrySave(NewSnapshot(version: 4, payload: "older")));

        Assert.Equal("newer", store.Load("room-1")!.Payload);
        Assert.Equal(5, store.Load("room-1")!.Version);
    }

    [Fact]
    public void Save_StaleVersion_ThrowsForPromisedWrites()
    {
        // 已对上游承诺成功的写入走 Save：旧版本写入是故障，fail-visible 抛错不静默。
        var store = NewStore();
        store.Save(NewSnapshot(version: 2));

        Assert.Throws<InvalidOperationException>(() => store.Save(NewSnapshot(version: 2, payload: "older")));
    }

    [Fact]
    public void Save_HigherVersion_Overwrites()
    {
        var store = NewStore();
        store.Save(NewSnapshot(version: 1, payload: "v1"));

        Assert.True(store.TrySave(NewSnapshot(version: 2, payload: "v2")));
        Assert.Equal(2, store.Load("room-1")!.Version);
    }

    [Fact]
    public void Load_UnknownRoom_ReturnsNull()
    {
        var store = NewStore();

        Assert.Null(store.Load("ghost"));
    }

    [Fact]
    public void Load_CorruptBackendPayload_ThrowsIntegrityViolation()
    {
        // 后端返回被篡改的记录时必须 fail-visible 抛错——不能被当成「未持久化」而触发错误重建。
        var store = new SnapshotStore(new TamperingBackend());

        Assert.Throws<SnapshotIntegrityViolationException>(() => store.Load("room-1"));
    }

    [Fact]
    public void Delete_Existing_RemovesRecord()
    {
        var store = NewStore();
        store.Save(NewSnapshot());

        Assert.True(store.Delete("room-1"));
        Assert.Null(store.Load("room-1"));
        Assert.Empty(store.ListRoomIds());
    }

    [Fact]
    public void Delete_UnknownRoom_ReturnsFalse()
    {
        var store = NewStore();

        Assert.False(store.Delete("ghost"));
    }

    [Fact]
    public void ListRoomIds_IsSortedAndUnique()
    {
        var store = NewStore();
        store.Save(NewSnapshot("room-c", version: 1));
        store.Save(NewSnapshot("room-a", version: 1));
        store.Save(NewSnapshot("room-b", version: 1));
        // 同房间多次覆盖版本不会产生重复房间。
        store.Save(NewSnapshot("room-a", version: 2));

        Assert.Equal(new[] { "room-a", "room-b", "room-c" }, store.ListRoomIds());
    }

    [Fact]
    public void Integrity_Compute_IsDeterministicAndDistinguishesPayloads()
    {
        var expected = SnapshotIntegrity.Compute(Payload);

        Assert.Equal(expected, SnapshotIntegrity.Compute(Payload));
        Assert.NotEqual(expected, SnapshotIntegrity.Compute(Payload + " "));

        // 算法固定为 SHA-256 前 8 字节（大端 long）：跨进程/跨版本稳定，快照可离线校验。
        var sha = SHA256.HashData(Encoding.UTF8.GetBytes(Payload));
        Assert.Equal(BinaryPrimitives.ReadInt64BigEndian(sha.AsSpan(0, 8)), expected);
    }

    [Fact]
    public void Integrity_Verify_TrueForCanonical_FalseForTampered()
    {
        var canonical = NewSnapshot(checksum: SnapshotIntegrity.Compute(Payload));

        Assert.True(SnapshotIntegrity.Verify(canonical));
        Assert.False(SnapshotIntegrity.Verify(NewSnapshot(checksum: 0xCAFEBABEL)));
        Assert.False(SnapshotIntegrity.Verify(canonical with { Payload = Payload + "tampered" }));
    }

    [Fact]
    public void Save_PutsCanonicalRecordInBackend()
    {
        var backend = new InMemorySnapshotBackend();
        var store = new SnapshotStore(backend);
        store.Save(NewSnapshot(version: 3));

        Assert.Equal("room-1", backend.TryGet("room-1")!.RoomId);
        Assert.Equal(3, backend.TryGet("room-1")!.Version);
        Assert.Equal(SnapshotIntegrity.Compute(Payload), backend.TryGet("room-1")!.Checksum);
    }

    private sealed class TamperingBackend : ISnapshotBackend
    {
        public StateSnapshot? TryGet(string roomId) => roomId == "room-1"
            ? new StateSnapshot("room-1", 1, 1, "corrupted", SnapshotIntegrity.Compute("original"))
            : null;

        public void Put(StateSnapshot snapshot) { }

        public bool Delete(string roomId) => false;

        public IReadOnlyList<string> ListRoomIds() => ["room-1"];
    }
}

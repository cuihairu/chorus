using System.Text.Json;
using System.Text.Json.Serialization;
using Chorus.Engine.Routing;
using Chorus.Engine.Session;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Models;

namespace Chorus.Control;

public sealed record NodeReport(string Id, string InstanceId, string MachineId, string Role,
    IpcNetworkEndpoint Endpoint, int Capacity, long LastHeartbeatMs = 0, bool Draining = false,
    double CpuPercent = 0, long MemoryBytes = 0, string Version = "", IReadOnlyList<SessionDiagnostics>? Sessions = null);
public sealed record RoomRecord(string Id, string WorkerId, string WorkerInstance, long Epoch,
    RoomLifecycleState State, long UpdatedAtMs, string? StreamerId = null, string Platform = "unknown",
    string Mode = "default", string Region = "default", string? MatchId = null,
    long TickId = 0, JsonElement? Snapshot = null, long Checksum = 0, int SchemaVersion = 1,
    IReadOnlyList<AudienceEvent>? Inbox = null, IReadOnlyDictionary<string, long>? Receipts = null,
    string? MigrationId = null, string? TargetWorker = null, string? PreviousWorker = null,
    RoomLifecycleState? ResumeState = null, bool RetirementRequested = false);
public sealed record OwnerRequest(string WorkerId, string InstanceId, long Epoch);
public sealed record TickCommit(string WorkerId, string InstanceId, long Epoch, long ExpectedTick,
    long TickId, int SchemaVersion, JsonElement State, IReadOnlyList<string> ConsumedEventKeys);
public sealed record RoomAssignment(string Id, string? StreamerId = null, string Platform = "unknown", string Mode = "default", string Region = "default");
public sealed record RoomMove(string TargetWorker, long ExpectedEpoch);
public sealed record RoomAction(RoomLifecycleAction Action, long ExpectedEpoch);
public sealed record ConnectionRecord(string SessionId, string ClientId, string UserId, string Role,
    string GatewayId, string GatewayInstance, string Generation, long LastSeenMs = 0);
public sealed record StreamerProfile(string Id, string DisplayName, string Platform, string AccountId,
    long Experience = 0, long Influence = 0, int Rating = 1000, int Games = 0, int Wins = 0,
    long Version = 1, long UpdatedAtMs = 0);
public sealed record MatchRules(long Version = 1, string Season = "default", int TierWidth = 200,
    long WidenAfterMs = 30000, int MaxRatingGap = 400, long TimeoutMs = 120000,
    bool FillBots = false, int LevelExperience = 100, int StarExperience = 20, long RematchCooldownMs = 60000);
public sealed record QueueEntry(string StreamerId, string Mode, string Region, string Platform, long JoinedAtMs, string Status = "Queued", string Reason = "Waiting for same-tier opponent");
public sealed record MatchRecord(string Id, string Mode, string Region, string Season, long RulesVersion,
    IReadOnlyList<string> StreamerIds, IReadOnlyList<string> RoomIds, long CreatedAtMs, int RatingGap,
    bool CrossTier, bool FilledWithBots, string Status = "Running", string? WinnerId = null, long? FinishedAtMs = null,
    long? ReadyDeadlineMs = null, IReadOnlyList<QueueEntry>? QueueEntries = null, bool RoomsRetired = false);
public sealed record MatchJoin(string Mode = "default", string Region = "default");
public sealed record MatchFinish(string? WinnerId, string SourceId);
public sealed record ScoreChange(string ParticipantId, long Delta, string SourceId, string Unit = "points");
public sealed record StoredBoard(string Id, string Scope, string Unit, string Season,
    IReadOnlyDictionary<string, long> Scores,
    [property: JsonConverter(typeof(ReadOnlyStringSetConverter))] IReadOnlySet<string> Sources, long UpdatedAtMs, bool Frozen = false);
public sealed record AuditRecord(long Id, long AtMs, string Actor, string Action, string Target, string Detail);
public sealed record AuditStoreUsage(long Rows, long ApproximatePayloadBytes, long? OldestAtMs, long? NewestAtMs, bool CapacityWarning);
public sealed record SummarySample(long AtMs, int Machines, int Workers, int Gateways, int Rooms, int ActiveRooms, int Connections, int OnlineUsers, int OnlineStreamers, int Queued, int RunningMatches,
    IReadOnlyDictionary<string, int>? RoomsByPlatform = null, IReadOnlyDictionary<string, int>? OnlineUsersByPlatform = null);
public sealed record EventHeatSample(long BucketAtMs, string Platform, AudienceEventKind Kind, long Events, long GiftUnits);
public sealed record EventHeatAlert(string Id, string Platform, long BucketAtMs, long Events, long Threshold, bool Acknowledged, long? AcknowledgedAtMs);
public sealed record EventHeatAlertAckRequest(string Platform, long BucketAtMs);
public static class ControlDimensions
{
    private static readonly HashSet<string> Platforms = new(StringComparer.Ordinal) { "bilibili", "douyin", "kuaishou", "test", "other" };
    public static string PlatformBucket(string? platform)
    {
        var normalized = platform?.Trim().ToLowerInvariant();
        return normalized is not null && Platforms.Contains(normalized) && normalized != "other" ? normalized : "other";
    }
    public static bool IsPlatformBucket(string platform) => Platforms.Contains(platform.Trim().ToLowerInvariant());
}
public sealed record ControlRoutingData(long SnapshotAtMs, IReadOnlyList<NodeReport> Nodes,
    IReadOnlyList<RoomRecord> Rooms, IReadOnlyList<ConnectionRecord> Connections);
public sealed class ControlConflictException(string message) : Exception(message);

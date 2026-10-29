using System.Text.Json.Serialization;

namespace Chorus.Redesign.StreamerGateway.Messages;

/// <summary>Hello (contracts 3.1): the first frame on every connection. The protocol version
/// gates everything that follows; the build and module versions are diagnostics.</summary>
public sealed record EdgeHello
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Hello;
    public int ProtocolVersion { get; init; }
    public string? ClientBuild { get; init; }
    public IReadOnlyList<string> SupportedModuleVersions { get; init; } = [];
}

/// <summary>Ping: proves the connection is reachable, nothing more (contracts 3.1). The
/// gateway answers with a pong and makes no session inference from either frame.</summary>
public sealed record EdgePing;

/// <summary>Pong: the ping reply with the server clock, for the client's own lag math.</summary>
public sealed record EdgePong
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Pong;
    public long ServerTimeMs { get; init; }
}

/// <summary>Error (contracts 1, 3.2): a visible refusal. Codes come from
/// <see cref="EdgeErrorCodes"/>; the message is short and safe to show.</summary>
public sealed record EdgeError
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Error;
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? OperationId { get; init; }
}

/// <summary>LoginWithPlatformLaunch (contracts 3.1): the client's first authenticated move.
/// The client generated and stored the next resume secret before sending (3.3); it rides as
/// base64 and is never logged.</summary>
public sealed record EdgeLoginWithPlatformLaunch
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.LoginWithPlatformLaunch;
    public string? Platform { get; init; }
    public required string AppId { get; init; }
    public required string LaunchCredential { get; init; }
    public string? LoginAttemptId { get; init; }
    public byte[]? NextResumeSecret { get; init; }
}

/// <summary>Resume (contracts 3.1, 3.3): rotate onto a fresh session with a new secret. The
/// room facts are diagnostics only — the server's participation record decides.</summary>
public sealed record EdgeResume
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Resume;
    public required string ResumeOperationId { get; init; }
    public byte[]? ResumeCredential { get; init; }
    public byte[]? NextResumeSecret { get; init; }
    public required string PreviousSessionId { get; init; }
    public string? LastSeenRoomId { get; init; }
    public long? LastAppliedStateVersion { get; init; }
}

/// <summary>Logout (contracts 3.1): ends the session; the gateway closes the connection after
/// the revocation is durable. No downlink acknowledgement exists for it.</summary>
public sealed record EdgeLogout
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Logout;
    public string? OperationId { get; init; }
}

/// <summary>LoginReady (contracts 3.2): the session facts the client keeps. The access
/// credential names the acting session for every later uplink.</summary>
public sealed record EdgeLoginReady
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.LoginReady;
    public required string StreamerId { get; init; }
    public required string SessionId { get; init; }
    public long SessionVersion { get; init; }
    public required string AccessCredential { get; init; }
    public long AccessExpiresAt { get; init; }
    public bool ResumeRegistered { get; init; }
}

/// <summary>SessionReplaced (contracts 3.2): another login or resume displaced this
/// connection; it arrives right before the gateway closes it.</summary>
public sealed record EdgeSessionReplaced
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.SessionReplaced;
    public required string Reason { get; init; }
}

/// <summary>StartGameplay (contracts 3.1): mount the live session onto a game. The rules
/// version folds into the match partition later — candidates on different rules never meet
/// (contracts 2, Match/ControlServer row).</summary>
public sealed record EdgeStartGameplay
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.StartGameplay;
    public required string OperationId { get; init; }
    public required string GameId { get; init; }
    public string? RulesVersion { get; init; }
}

/// <summary>JoinQueue (contracts 3.1): enqueue the mount for matching. The options blob is
/// opaque to the edge — the partition the gateway composes carries the facts it can name.</summary>
public sealed record EdgeJoinQueue
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.JoinQueue;
    public required string OperationId { get; init; }
    public required string QueueType { get; init; }
    public string? Options { get; init; }
}

/// <summary>CancelQueue (contracts 3.1): withdraw the mount's ticket. The ticket facts the
/// client holds are diagnostics — the mount's own ticket is what the server cancels.</summary>
public sealed record EdgeCancelQueue
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.CancelQueue;
    public required string OperationId { get; init; }
    public string? TicketId { get; init; }
    public long? TicketVersion { get; init; }
}

/// <summary>GameplayStatus (contracts 3.2): the mount facts as the binding store sees them.
/// Arrives as the startGameplay reply; the state vocabulary is the binding store's.</summary>
public sealed record EdgeGameplayStatus
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.GameplayStatus;
    public required string BindingId { get; init; }
    public long Generation { get; init; }
    public required string State { get; init; }
    public string? Reason { get; init; }
}

/// <summary>MatchStatus (contracts 3.2): where the mount's ticket stands. "Matched" is the
/// edge's word for the store's Committed — the round exists, the room id names it.</summary>
public sealed record EdgeMatchStatus
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.MatchStatus;
    public required string TicketId { get; init; }
    public required string State { get; init; }
    public string? ReservationId { get; init; }
    public string? RoomId { get; init; }
}

/// <summary>Ack (contracts 3.1): the client's applied position on one room's chain. The edge
/// records it as the connection's re-baseline fact; it is never answered.</summary>
public sealed record EdgeAck
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Ack;
    public required string RoomId { get; init; }
    public long RoomEpoch { get; init; }
    public long LastAppliedTickId { get; init; }
    public long StateVersion { get; init; }
}

/// <summary>RequestSnapshot (contracts 3.1): the client asks to be re-baselined. The edge
/// re-subscribes the room at zero so the authoritative head snapshot supersedes the chain.</summary>
public sealed record EdgeRequestSnapshot
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.RequestSnapshot;
    public required string RoomId { get; init; }
    public long RoomEpoch { get; init; }
    public string? Reason { get; init; }
}

/// <summary>RoomJoined (contracts 3.2): the room facts a connection needs before any frame —
/// which side it plays, and the module identity the frames will carry.</summary>
public sealed record EdgeRoomJoined
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.RoomJoined;
    public required string RoomId { get; init; }
    public long RoomEpoch { get; init; }
    public required string Participation { get; init; }
    public required string ModuleVersion { get; init; }
    public required string ConfigHash { get; init; }
}

/// <summary>Snapshot (contracts 3.2): the full authoritative state at one tick. The payload is
/// the module's serialized state, carried as base64 like every byte field on the wire.</summary>
public sealed record EdgeSnapshot
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Snapshot;
    public required string RoomId { get; init; }
    public long RoomEpoch { get; init; }
    public long TickId { get; init; }
    public long StateVersion { get; init; }
    public int SchemaVersion { get; init; }
    public required byte[] Payload { get; init; }
}

/// <summary>Delta (contracts 3.2): one committed frame. The payload is the frame's effects as
/// a JSON array — the module's own delta vocabulary, not an edge invention.</summary>
public sealed record EdgeDelta
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Delta;
    public required string RoomId { get; init; }
    public long RoomEpoch { get; init; }
    public long TickId { get; init; }
    public long BaseVersion { get; init; }
    public long StateVersion { get; init; }
    public required System.Text.Json.JsonElement Payload { get; init; }
}

/// <summary>RoundResult (contracts 3.2): the frozen result. The result id is composed from the
/// immutable coordinate triple (room, epoch, tick) — the settlement version itself lives
/// behind the CommitResult boundary the sync stream does not carry.</summary>
public sealed record EdgeRoundResult
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.RoundResult;
    public required string ResultId { get; init; }
    public required string SettlementState { get; init; }
    public required System.Text.Json.JsonElement Result { get; init; }
}

/// <summary>RoomSuspended (contracts 3.2): the room parked itself visibly instead of dying.
/// The recovery attempt is the room's epoch — every recovery lands on a bumped one.</summary>
public sealed record EdgeRoomSuspended
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.RoomSuspended;
    public string? Reason { get; init; }
    public long RecoveryAttempt { get; init; }
}

/// <summary>ResyncRequired (contracts 3.2): the chain the client holds cannot continue; only a
/// fresh snapshot of the current room can put it back on one.</summary>
public sealed record EdgeResyncRequired
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.ResyncRequired;
    public required string RoomId { get; init; }
    public long RoomEpoch { get; init; }
    public string? Reason { get; init; }
}

/// <summary>GameCommand (contracts 3.1): one durable command into the round the mount plays.
/// The session and the participation validate in the store's transaction; the identity fields
/// a client might attach are ignored — the mount's authenticated facts are the address.</summary>
public sealed record EdgeGameCommand
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.GameCommand;
    public required string CommandId { get; init; }
    public required string RoomId { get; init; }
    public long RoomEpoch { get; init; }
    public long ClientSequence { get; init; }
    public required System.Text.Json.JsonElement Payload { get; init; }
}

/// <summary>LeaveRound (contracts 3.1): withdraw from the round; the room runs to its natural
/// settlement without the leaver. The reason is diagnostics the client names, never a fact the
/// plane acts on.</summary>
public sealed record EdgeLeaveRound
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.LeaveRound;
    public required string OperationId { get; init; }
    public required string RoomId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>CommandReceipt (contracts 3.2): the two words a command hears. Accepted means
/// durably in the round's input record — not applied; Applied rides right after the delta that
/// consumed the command. The receipt id is the room and the store-assigned sequence, so an
/// exact retry replays the same receipt.</summary>
public sealed record EdgeCommandReceipt
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.CommandReceipt;
    public required string CommandId { get; init; }
    public required string Status { get; init; }
    public required string ReceiptId { get; init; }
}

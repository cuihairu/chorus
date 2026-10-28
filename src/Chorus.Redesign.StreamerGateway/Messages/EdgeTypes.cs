namespace Chorus.Redesign.StreamerGateway.Messages;

/// <summary>
/// The wire names of the client protocol (contracts 3.1, 3.2). Every frame is a single JSON
/// text message whose "type" field selects one of these; the field names inside each message
/// are camelCase and line up with the contract definitions. The set is closed: a frame whose
/// type is not listed here is a protocol violation, not a future message to ignore.
/// </summary>
public static class EdgeTypes
{
    // Uplink (contracts 3.1).
    public const string Hello = "hello";
    public const string LoginWithPlatformLaunch = "loginWithPlatformLaunch";
    public const string Resume = "resume";
    public const string StartGameplay = "startGameplay";
    public const string JoinQueue = "joinQueue";
    public const string CancelQueue = "cancelQueue";
    public const string GameCommand = "gameCommand";
    public const string Ack = "ack";
    public const string RequestSnapshot = "requestSnapshot";
    public const string LeaveRound = "leaveRound";
    public const string Logout = "logout";
    public const string Ping = "ping";

    // Downlink (contracts 3.2) plus the transport-level replies.
    public const string LoginReady = "loginReady";
    public const string GameplayStatus = "gameplayStatus";
    public const string MatchStatus = "matchStatus";
    public const string RoomJoined = "roomJoined";
    public const string Snapshot = "snapshot";
    public const string Delta = "delta";
    public const string CommandReceipt = "commandReceipt";
    public const string SessionReplaced = "sessionReplaced";
    public const string RoomSuspended = "roomSuspended";
    public const string RoundResult = "roundResult";
    public const string ResyncRequired = "resyncRequired";
    public const string Pong = "pong";
    public const string Error = "error";
}

/// <summary>The protocol versions this build speaks. A mismatched hello is rejected before
/// anything else is read (contracts 3.1: Hello carries the version the client wants).</summary>
public static class EdgeProtocolVersion
{
    public const int Current = 1;
}

/// <summary>
/// The error codes of the protocol. An error frame carries only these codes with a short
/// message and the operation id when one was in flight — never SQL, paths or credentials
/// (contracts 1).
/// </summary>
public static class EdgeErrorCodes
{
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string MessageTooLarge = "message_too_large";
    public const string RateLimited = "rate_limited";
    public const string NoSession = "no_session";
    public const string InvalidMessage = "invalid_message";
    public const string GatewayFull = "gateway_full";
}

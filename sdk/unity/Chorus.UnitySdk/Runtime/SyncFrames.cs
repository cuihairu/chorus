using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>The sync frame kinds the stream can carry: the SyncMessage oneof members,
    /// plus the edge wire's own lane frame for a parked room (that lane is the edge
    /// transport's vocabulary, not a SyncMessage member — the parity pins name it
    /// deliberately).</summary>
    public static class SyncFrameKind
    {
        /// <summary>A full snapshot the client rebaselines on.</summary>
        public const string Snapshot = "snapshot";

        /// <summary>One authoritative frame's incremental payload.</summary>
        public const string Delta = "delta";

        /// <summary>The frozen round result; the room is terminal from now on.</summary>
        public const string RoundResult = "round_result";

        /// <summary>The stream cannot continue on the subscriber's epoch; resubscribe.</summary>
        public const string ResyncRequired = "resync_required";

        /// <summary>The room parked itself visibly (a pause, a fault): the chain's story
        /// ends here and the room returns — if it returns — on the bumped epoch named by
        /// the recovery attempt. The game handles this frame itself; the applier never
        /// sees it.</summary>
        public const string Suspended = "roomSuspended";
    }

    /// <summary>
    /// One message on the authoritative sync stream (the server's SyncMessage family,
    /// restated platform-neutrally — the parity test pins every field against the server's
    /// contract). Exactly one member besides <see cref="Kind"/> is set.
    /// </summary>
    public sealed class SyncFrame
    {
        /// <summary>Which member carries the message: a <see cref="SyncFrameKind"/> value.</summary>
        public string Kind { get; set; } = SyncFrameKind.Snapshot;

        /// <summary>The snapshot, when <see cref="Kind"/> is snapshot.</summary>
        public SnapshotFrame? Snapshot { get; set; }

        /// <summary>The delta, when <see cref="Kind"/> is delta.</summary>
        public DeltaFrame? Delta { get; set; }

        /// <summary>The round result, when <see cref="Kind"/> is round_result.</summary>
        public RoundResultFrame? RoundResult { get; set; }

        /// <summary>The resync order, when <see cref="Kind"/> is resync_required.</summary>
        public ResyncRequiredFrame? ResyncRequired { get; set; }

        /// <summary>The park notice, when <see cref="Kind"/> is roomSuspended — the lane
        /// member of the edge wire, delivered as the subscription's last frame.</summary>
        public RoomSuspendedFrame? Suspended { get; set; }
    }

    /// <summary>A full snapshot. Every message carries the room identity the client validates:
    /// room, epoch, tick, state version, module version and config hash.</summary>
    public sealed class SnapshotFrame
    {
        public string RoomId { get; set; } = string.Empty;
        public long Epoch { get; set; }
        public long Tick { get; set; }
        public long StateVersion { get; set; }
        public string ModuleVersion { get; set; } = string.Empty;
        public string ConfigHash { get; set; } = string.Empty;
        /// <summary>The module's own serialized state — opaque to the SDK, decoded by the game module's applier.</summary>
        public byte[] StatePayload { get; set; } = Array.Empty<byte>();
        public int StateSchemaVersion { get; set; }
    }

    /// <summary>One authoritative frame applied on top of the baseline.</summary>
    public sealed class DeltaFrame
    {
        public string RoomId { get; set; } = string.Empty;
        public long Epoch { get; set; }
        public long Tick { get; set; }
        public long StateVersion { get; set; }
        /// <summary>The version this delta applies to — a delta whose base the client never applied is a gap, not an error.</summary>
        public long BaseVersion { get; set; }
        public string ModuleVersion { get; set; } = string.Empty;
        public string ConfigHash { get; set; } = string.Empty;
        /// <summary>The frame's committed state hash, carried for diagnosis.</summary>
        public byte[] StateHash { get; set; } = Array.Empty<byte>();
        /// <summary>The platform event ids the frame consumed (dedup and diagnosis).</summary>
        public string[] InputEventIds { get; set; } = Array.Empty<string>();
        /// <summary>The frame's visual effects, in frame order.</summary>
        public FrameEffect[] Effects { get; set; } = Array.Empty<FrameEffect>();
        /// <summary>The client commands the frame consumed — the receipt the sender matches on.</summary>
        public CommandReceipt[] Commands { get; set; } = Array.Empty<CommandReceipt>();
    }

    /// <summary>One frame effect: a typed payload the game renders.</summary>
    public sealed class FrameEffect
    {
        public string Kind { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
    }

    /// <summary>Acknowledges that a client command became part of the authoritative frame record.</summary>
    public sealed class CommandReceipt
    {
        public string CommandId { get; set; } = string.Empty;
        public long Sequence { get; set; }
    }

    /// <summary>The frozen result of the round: the room is terminal after it.</summary>
    public sealed class RoundResultFrame
    {
        public string RoomId { get; set; } = string.Empty;
        public long Epoch { get; set; }
        public long Tick { get; set; }
        public long StateVersion { get; set; }
        public string ResultJson { get; set; } = string.Empty;
    }

    /// <summary>The stream refuses the subscriber's epoch: resubscribe at the current one.</summary>
    public sealed class ResyncRequiredFrame
    {
        public string RoomId { get; set; } = string.Empty;
        public long CurrentEpoch { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>The park notice (the edge wire's roomSuspended lane): the room parked
    /// itself visibly instead of dying — a pause, a fault. A business pause is not a
    /// fault; both surface here. The recovery attempt is the room's epoch: every
    /// recovery lands on a bumped one, and the game's way back in is the resume leg it
    /// already holds (reconnect, re-enter, resubscribe at the new epoch).</summary>
    public sealed class RoomSuspendedFrame
    {
        /// <summary>Why the room's stream ended, as the edge phrased it.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>The epoch the recovery will land on.</summary>
        public long RecoveryAttempt { get; set; }
    }

    /// <summary>One client intent on its way into the room inbox — a command the authoritative
    /// frame will consume and receipt on the delta that ate it.</summary>
    public sealed class ClientIntent
    {
        /// <summary>The session the intent rides (the login's session id).</summary>
        public string SessionId { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public long RoomEpoch { get; set; }
        public string GameId { get; set; } = string.Empty;
        /// <summary>The client's own idempotency key — one intent id is consumed exactly once.</summary>
        public string CommandId { get; set; } = string.Empty;
        /// <summary>The intent's payload as the game module defines it.</summary>
        public string PayloadJson { get; set; } = string.Empty;
        // The tenant rides the session's configuration (SdkConfiguration.Tenant), not the
        // intent: the transport stamps it, a game cannot cross tenants by accident.
    }

    /// <summary>What the input face answers for one intent.</summary>
    public sealed class IntentReply
    {
        public string CommandId { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public long Sequence { get; set; }
        public long RouteVersion { get; set; }
        /// <summary>True when the command id was already consumed — the receipt, not a second application.</summary>
        public bool Duplicate { get; set; }
        /// <summary>The delivery row's own event id, distinct from the client's command id.</summary>
        public string EventId { get; set; } = string.Empty;
    }

    /// <summary>What the login face answers: the Chorus session the platform mount was exchanged for.</summary>
    public sealed class LoginAnswer
    {
        public string StreamerId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public long SessionVersion { get; set; }
        /// <summary>The Chorus-issued credential every later call rides — server-issued, platform-agnostic.</summary>
        public string AccessCredential { get; set; } = string.Empty;
        public long AccessExpiresAtUnixMs { get; set; }
        /// <summary>True when this login repeated a session the server had already granted.</summary>
        public bool Duplicate { get; set; }
        /// <summary>The resume rotation receipt was committed with the grant; the reply implies durability.</summary>
        public bool ResumeRegistered { get; set; }
    }
}

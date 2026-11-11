using System;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The Unity side's sync applier (FIX-U3D-09): a name-for-name port of the server's
    /// reference applier (Chorus.Client.BaselineApplier), answering exactly what the
    /// reference answers on exactly the same inputs. The client only advances state on a
    /// frame that matches its room, epoch and baseline: duplicates and regressions drop,
    /// a version jump or a missing baseline asks for a full snapshot, and a different
    /// epoch never chains onto the old baseline — epochs are ownership generations, not
    /// ordered versions. The chain is keyed on state version alone; tick rides along for
    /// display (a version can advance while the tick pauses). Single receive-loop use,
    /// no locks — game code decides every frame from its own loop.
    /// </summary>
    public sealed class SyncApplier : ISyncApplier
    {
        private readonly string _roomId;
        private readonly long _epoch;
        private readonly DeltaContractMode _deltaContract;
        private long _tick = -1;
        private long _stateVersion = -1;
        private string? _moduleVersion;
        private string? _configHash;
        private bool _hasBaseline;
        private bool _terminal;

        public SyncApplier(string roomId, long epoch, DeltaContractMode deltaContract)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("A baseline needs a room id.", nameof(roomId));
            }

            if (epoch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "The epoch must not be negative.");
            }

            _roomId = roomId;
            _epoch = epoch;
            _deltaContract = deltaContract;
        }

        /// <summary>A copy of the current baseline (−1 tick/version before the first snapshot).</summary>
        public BaselineStatus Status => new BaselineStatus
        {
            RoomId = _roomId,
            Epoch = _epoch,
            Tick = _tick,
            StateVersion = _stateVersion,
            ModuleVersion = _moduleVersion,
            ConfigHash = _configHash,
            HasBaseline = _hasBaseline,
            Terminal = _terminal,
        };

        /// <summary>
        /// Decides one sync frame against the baseline. Apply decisions advance the
        /// baseline; drop, rebaseline and invalid decisions leave it untouched.
        /// </summary>
        public SyncDecision Decide(SyncFrame frame)
        {
            if (frame is null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Snapshot is { } snapshot)
            {
                return DecideSnapshot(snapshot);
            }

            if (frame.Delta is { } delta)
            {
                return DecideDelta(delta);
            }

            if (frame.RoundResult is { } result)
            {
                return DecideResult(result);
            }

            if (frame.ResyncRequired is { } resync)
            {
                return DecideResync(resync);
            }

            return SyncDecision.Invalid(SyncReasons.ProtocolEmptyMessage);
        }

        private SyncDecision DecideSnapshot(SnapshotFrame snapshot)
        {
            if (snapshot.RoomId != _roomId)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolRoomMismatch);
            }

            if (snapshot.Epoch != _epoch)
            {
                return SyncDecision.Rebaseline(SyncReasons.CrossEpoch, snapshot.Epoch);
            }

            if (snapshot.StateVersion < 1 || snapshot.Tick < 0)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolVersionRange);
            }

            if (_terminal)
            {
                return SyncDecision.Drop(SyncReasons.Duplicate);
            }

            if (!_hasBaseline)
            {
                return ApplySnapshot(snapshot);
            }

            if (snapshot.ModuleVersion != _moduleVersion || snapshot.ConfigHash != _configHash)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolModuleConfigMismatch);
            }

            if (snapshot.StateVersion > _stateVersion && snapshot.Tick < _tick)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolTickRegression);
            }

            if (snapshot.StateVersion < _stateVersion)
            {
                return SyncDecision.Drop(SyncReasons.Regression);
            }

            if (snapshot.StateVersion == _stateVersion)
            {
                return SyncDecision.Drop(SyncReasons.Duplicate);
            }

            return ApplySnapshot(snapshot);
        }

        private SyncDecision DecideDelta(DeltaFrame delta)
        {
            if (delta.RoomId != _roomId)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolRoomMismatch);
            }

            if (delta.Epoch != _epoch)
            {
                return SyncDecision.Rebaseline(SyncReasons.CrossEpoch, delta.Epoch);
            }

            if (delta.StateVersion < 1 || delta.Tick < 0)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolVersionRange);
            }

            if (_terminal)
            {
                return SyncDecision.Drop(SyncReasons.Duplicate);
            }

            if (_deltaContract == DeltaContractMode.SnapshotOnly)
            {
                return SyncDecision.Rebaseline(SyncReasons.NoDeltaContract);
            }

            if (!_hasBaseline)
            {
                return SyncDecision.Rebaseline(SyncReasons.MissingBaseline);
            }

            if (delta.ModuleVersion != _moduleVersion || delta.ConfigHash != _configHash)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolModuleConfigMismatch);
            }

            // The server commits stateVersion = base + 1 and tick = lastTick + 1 in one
            // transaction (RedesignFrameStore): a delta outside the invariant is corruption,
            // not a gap. The invariant also collapses base-vs-current — a duplicate delta
            // (base = current-1) lands in the version == current drop below instead of being
            // misread as a missing baseline.
            if (delta.StateVersion != delta.BaseVersion + 1)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolDeltaInvariant);
            }

            if (delta.StateVersion < _stateVersion)
            {
                return SyncDecision.Drop(SyncReasons.Regression);
            }

            if (delta.StateVersion == _stateVersion)
            {
                return SyncDecision.Drop(SyncReasons.Duplicate);
            }

            if (delta.StateVersion > _stateVersion + 1)
            {
                return SyncDecision.Rebaseline(SyncReasons.Gap);
            }

            // Exactly current + 1: a lower tick is impossible from a valid commit, and a
            // version-only pause boundary (tick unchanged) is legal.
            if (delta.Tick < _tick)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolTickRegression);
            }

            _tick = delta.Tick;
            _stateVersion = delta.StateVersion;
            return SyncDecision.Apply(SyncDecisionKind.ApplyDelta);
        }

        private SyncDecision DecideResult(RoundResultFrame result)
        {
            if (result.RoomId != _roomId)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolRoomMismatch);
            }

            if (result.Epoch != _epoch)
            {
                return SyncDecision.Rebaseline(SyncReasons.CrossEpoch, result.Epoch);
            }

            if (result.StateVersion < 1)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolVersionRange);
            }

            if (_terminal)
            {
                return SyncDecision.Drop(SyncReasons.Duplicate);
            }

            if (_hasBaseline && result.StateVersion < _stateVersion)
            {
                return SyncDecision.Drop(SyncReasons.Regression);
            }

            // A bare round result is a real flow for a released room that answered a correct
            // subscriber (RoomRuntime.LateSubscriberAnswer): the terminal marker applies even
            // with no state baseline behind it.
            _tick = result.Tick;
            _stateVersion = result.StateVersion;
            _terminal = true;
            return SyncDecision.Apply(SyncDecisionKind.ApplyResult);
        }

        private SyncDecision DecideResync(ResyncRequiredFrame resync)
        {
            if (resync.RoomId != _roomId)
            {
                return SyncDecision.Invalid(SyncReasons.ProtocolRoomMismatch);
            }

            return SyncDecision.Rebaseline(SyncReasons.Resync, resync.CurrentEpoch);
        }

        private SyncDecision ApplySnapshot(SnapshotFrame snapshot)
        {
            _tick = snapshot.Tick;
            _stateVersion = snapshot.StateVersion;
            _moduleVersion = snapshot.ModuleVersion;
            _configHash = snapshot.ConfigHash;
            _hasBaseline = true;
            return SyncDecision.Apply(SyncDecisionKind.ApplySnapshot);
        }
    }
}

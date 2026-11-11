using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk.Samples
{
    /// <summary>
    /// The knobs the online sample's waits ride (FIX-U3D-15). Every default is generous
    /// enough for a shared development cluster; the budgets exist so a stuck link ends
    /// the sample with a story instead of hanging a game's main thread.
    /// </summary>
    public sealed class SampleOptions
    {
        /// <summary>The match partition the sample queues into.</summary>
        public string MatchQueue { get; set; } = "standard";

        /// <summary>The boost level the sample plays (the example module's move).</summary>
        public int BoostLevel { get; set; } = 3;

        /// <summary>The example module's board the sample reads after the round settles.
        /// A real game names its own boards here.</summary>
        public string BoardId { get; set; } = "faction-score";

        /// <summary>How long one RPC-shaped wait may take (match, entry, subscribe, one
        /// read) before the sample reports the leg as stuck.</summary>
        public TimeSpan StepBudget { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>How long one wait-for-word leg waits (a baseline, enough deltas, the
        /// round's frozen result) before the sample moves on with what it has.</summary>
        public TimeSpan PumpBudget { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>How long the resume's re-entry may stay quiet before the sample
        /// concludes the round was already over and walks on to the reads.</summary>
        public TimeSpan RejoinBudget { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>How long the sample stays on the room waiting for the round's frozen
        /// result before it leaves with the story so far.</summary>
        public TimeSpan RoundEndBudget { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How long the reads poll for the board's first page — the projection
        /// follows the round's settlement, it does not precede it.</summary>
        public TimeSpan BoardSettleBudget { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>What the sample's loop saw, leg by leg — the evidence a caller (or a
    /// fact) asserts on, and the story a stuck run prints before it returns.</summary>
    public sealed class SampleReport
    {
        public string SessionId { get; set; } = string.Empty;
        public string BindingId { get; set; } = string.Empty;
        public string TicketId { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public long RoomEpoch { get; set; }
        public string Participation { get; set; } = string.Empty;

        public bool BaselineApplied { get; set; }
        public long BaselineTick { get; set; }
        public long BaselineVersion { get; set; }

        public IList<long> IntentSequences { get; } = new List<long>();
        public bool AnyIntentDuplicate { get; set; }

        public int SnapshotsBeforeDisconnect { get; set; }
        public int DeltasBeforeDisconnect { get; set; }
        public long TickAtDisconnect { get; set; }
        public long VersionAtDisconnect { get; set; }

        public bool Rejoined { get; set; }
        public string RejoinedRoomId { get; set; } = string.Empty;
        public long RejoinedEpoch { get; set; }
        public bool RebaselineOnRejoin { get; set; }
        public int CatchupFrames { get; set; }
        public long VersionAfterCatchup { get; set; }

        public bool RoundEnded { get; set; }
        public string RoundResultJson { get; set; } = string.Empty;

        public int TopEntries { get; set; }
        public long LeadScore { get; set; }
        public bool LeadRankFound { get; set; }
        public long LeadRank { get; set; }
        public bool OwnRankFound { get; set; }
        public int CursorPageEntries { get; set; }
        public int AroundEntries { get; set; }

        /// <summary>The legs in the order the sample walked them — the human-readable
        /// trace a support ticket ships alongside the report. Each leg names its moment:
        /// milliseconds since the run began.</summary>
        public IList<string> Story { get; } = new List<string>();

        private readonly DateTime _startedAt = DateTime.UtcNow;

        internal void AddLeg(string leg) =>
            Story.Add("[" + (long)(DateTime.UtcNow - _startedAt).TotalMilliseconds + "ms] " + leg);
    }

    /// <summary>
    /// The online sample (FIX-U3D-15): one complete session — login, match, atomic room
    /// entry, snapshot baseline, moves, a disconnect and the resume's catch-up, the
    /// round's frozen result, the board reads, the exit — written once against the SDK's
    /// public face and driven by nothing but <see cref="SdkConfiguration"/> plus the
    /// platform adapter's own mount. This file is the shipped sample: a game copies it
    /// into its first scene and fills only the configuration.
    ///
    /// The one structural rule the wire imposes: while a room's stream is live, the
    /// game's own pump is the downlink's only reader — receipts and read answers ride
    /// the pumping ("a game that stops advancing the stream stops its own reads"). The
    /// sample therefore runs one pump per live stream for the stream's whole life and
    /// waits on the applier's words, exactly like a real game's main loop. Every wait is
    /// bounded by <see cref="SampleOptions"/> — a stuck link ends the run with the walk's
    /// story riding the failure (exception Data "chorus-story"), never a hung game.
    /// </summary>
    public static class SampleOnlineLoop
    {
        /// <summary>The frames the pump applied — the applier answers the baseline's
        /// version, the tally answers how it got there.</summary>
        private sealed class FrameTally
        {
            public int Snapshots;
            public int Deltas;
        }

        public static async Task<SampleReport> RunAsync(IChorusSdk sdk, SdkConfiguration configuration,
            IPlatformAdapter adapter, SampleOptions options, CancellationToken cancellationToken)
        {
            // The story rides any failure: a stuck link surfaces as the wait's own word,
            // and the legs the sample walked name where it stopped — the evidence a
            // support ticket ships with the stack.
            var report = new SampleReport();
            try
            {
                return await RunCoreAsync(sdk, configuration, adapter, options,
                    cancellationToken, report);
            }
            catch (Exception error)
            {
                error.Data["chorus-story"] = string.Join(" | ", report.Story);
                throw;
            }
        }

        private static async Task<SampleReport> RunCoreAsync(IChorusSdk sdk, SdkConfiguration configuration,
            IPlatformAdapter adapter, SampleOptions options, CancellationToken cancellationToken,
            SampleReport report)
        {
            if (sdk is null) throw new ArgumentNullException(nameof(sdk));
            if (configuration is null) throw new ArgumentNullException(nameof(configuration));
            if (adapter is null) throw new ArgumentNullException(nameof(adapter));
            if (options is null) throw new ArgumentNullException(nameof(options));

            // Connect: the platform adapter mounts, the SDK exchanges the mount for a
            // session and starts the gameplay binding. The resume secret rides out with
            // the session — persist both the moment you have them.
            var session = await sdk.ConnectAsync(configuration, adapter,
                Bounded(cancellationToken, options.StepBudget));
            report.SessionId = session.Login.SessionId;
            report.BindingId = session.BindingId ?? string.Empty;
            report.AddLeg("connected:" + report.SessionId);

            // Match and atomic entry: queue, then take the committed room and the seat's
            // facts in one answer — never one without the other.
            var ticket = await session.JoinMatchAsync(options.MatchQueue, null,
                Bounded(cancellationToken, options.StepBudget));
            report.TicketId = ticket.TicketId;
            report.AddLeg("queued:" + ticket.TicketId);
            var room = await session.AwaitRoomAsync(Bounded(cancellationToken, options.StepBudget));
            report.RoomId = room.RoomId;
            report.RoomEpoch = room.RoomEpoch;
            report.Participation = room.Participation;
            report.AddLeg("room:" + room.RoomId + "@" + room.RoomEpoch);

            // Subscribe at nothing applied: the first snapshot baselines the applier.
            // From here until the drop the sample keeps pumping — the downlink's only
            // reader is this loop, and everything (receipts, reads, results) rides it.
            var stream = await session.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0,
                Bounded(cancellationToken, options.StepBudget));
            var applier = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);
            var tally = new FrameTally();
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pump = PumpAsync(stream, applier, report, tally, pumpCts.Token);

            if (!await WaitForAsync(() => applier.Status.HasBaseline,
                    options.PumpBudget, cancellationToken))
                report.AddLeg("baseline:stuck");
            report.BaselineApplied = applier.Status.HasBaseline;
            report.BaselineTick = applier.Status.Tick;
            report.BaselineVersion = applier.Status.StateVersion;
            report.AddLeg("baseline:v" + report.BaselineVersion);

            // Moves: two boosts, each receipt carrying the sequence — the answers ride
            // the pump, like every downlink word while the stream is live. The command
            // id names the session: ids are unique across every client command on the
            // platform, so two devices never collide on a shared label.
            for (var move = 1; move <= 2; move++)
            {
                var reply = await session.SendIntentAsync(new ClientIntent
                {
                    RoomId = room.RoomId,
                    RoomEpoch = room.RoomEpoch,
                    CommandId = report.SessionId + "-boost-" + move,
                    PayloadJson = "{\"action\":\"boost\",\"level\":" + options.BoostLevel + "}",
                }, Bounded(cancellationToken, options.StepBudget));
                report.IntentSequences.Add(reply.Sequence);
                report.AnyIntentDuplicate |= reply.Duplicate;
            }
            report.AddLeg("boosted:" + report.IntentSequences.Count);

            // Play on: let the deltas the moves produced land, then remember where the
            // applier stands — the resume names exactly this version.
            await WaitForAsync(() => tally.Deltas >= 2, options.PumpBudget, cancellationToken);
            report.SnapshotsBeforeDisconnect = tally.Snapshots;
            report.DeltasBeforeDisconnect = tally.Deltas;
            report.TickAtDisconnect = applier.Status.Tick;
            report.VersionAtDisconnect = applier.Status.StateVersion;
            report.AddLeg("played:v" + report.VersionAtDisconnect);

            // Drop the wire — a resume, never a logout: the session survives server-side
            // and the held secret rotates onto a fresh one. The pump's socket goes with
            // the drop; the pump ends when its read fails.
            await session.DisconnectAsync();
            pumpCts.Cancel();
            await SwallowAsync(pump);
            report.AddLeg("disconnected");

            var resumed = await sdk.ResumeAsync(configuration, session.Login.SessionId,
                session.ResumeCredential, room.RoomId, report.VersionAtDisconnect,
                Bounded(cancellationToken, options.StepBudget));
            report.SessionId = resumed.Login.SessionId;
            report.AddLeg("resumed:" + report.SessionId);

            // The re-entry: the participation record names the room if its round still
            // runs. A quiet budget is the round's own word for "already over" — the
            // sample walks on to the reads either way.
            JoinedRoom? rejoined = null;
            try
            {
                rejoined = await resumed.AwaitRejoinedRoomAsync(
                    Bounded(cancellationToken, options.RejoinBudget));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                report.AddLeg("rejoin:quiet");
            }

            if (rejoined is not null)
            {
                report.Rejoined = true;
                report.RejoinedRoomId = rejoined.RoomId;
                report.RejoinedEpoch = rejoined.RoomEpoch;
                report.AddLeg("rejoined:" + rejoined.RoomId + "@" + rejoined.RoomEpoch);
                // Same epoch: the subscription continues the applier's baseline; a bumped
                // epoch (a recovery landed) rebaselines from the entry's own snapshot.
                var rebaseline = rejoined.RoomEpoch != room.RoomEpoch;
                report.RebaselineOnRejoin = rebaseline;
                var resumeApplier = rebaseline
                    ? new SyncApplier(rejoined.RoomId, rejoined.RoomEpoch, DeltaContractMode.Full)
                    : applier;
                var rejoinStream = await resumed.SubscribeAsync(rejoined.RoomId, rejoined.RoomEpoch,
                    applier.Status.Tick, applier.Status.StateVersion,
                    Bounded(cancellationToken, options.StepBudget));
                var catchTally = rebaseline ? new FrameTally() : tally;
                using var rejoinCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var rejoinPump = PumpAsync(rejoinStream, resumeApplier, report, catchTally,
                    rejoinCts.Token);
                // Stay on the room until the round freezes — the game plays on while the
                // ticks run; the frozen result is the room's own terminal word.
                if (!await WaitForAsync(() => resumeApplier.Status.Terminal || report.RoundEnded,
                        options.RoundEndBudget, cancellationToken))
                    report.AddLeg("round-result:stuck");
                report.CatchupFrames = catchTally.Snapshots + catchTally.Deltas
                    - (rebaseline ? 0 : report.SnapshotsBeforeDisconnect + report.DeltasBeforeDisconnect);
                report.VersionAfterCatchup = resumeApplier.Status.StateVersion;
                report.RoundEnded = resumeApplier.Status.Terminal;
                report.AddLeg("catch-up:v" + report.VersionAfterCatchup);
                // A round that never froze under this seat is left by name — the leave
                // rides the live pump while the room still serves words.
                if (!report.RoundEnded)
                {
                    await resumed.LeaveRoundAsync(report.RoomId,
                        Bounded(cancellationToken, options.StepBudget));
                    report.AddLeg("left-round");
                }
                // The room leg is over: the subscription ends at the terminal word (the
                // pump's read answers false) and the game drops the wire.
                await resumed.DisconnectAsync();
                rejoinCts.Cancel();
                await SwallowAsync(rejoinPump);
                report.AddLeg("room-closed");
                // The lobby leg comes back on the seat's own session — a resume lends a
                // fresh wire that never held the room's stream, and a stream-less
                // transport's query legs pump their own short loops. That is the lobby's
                // shape (a second login would replace the session still standing).
                var lobby = await sdk.ResumeAsync(configuration, resumed.Login.SessionId,
                    resumed.ResumeCredential, null, report.VersionAfterCatchup,
                    Bounded(cancellationToken, options.StepBudget));
                report.AddLeg("lobby:" + lobby.Login.SessionId);
                await ReadBoardAsync(lobby, report, options, cancellationToken);
                await lobby.LogoutAsync(Bounded(cancellationToken, options.StepBudget));
            }
            else
            {
                // No room to return to: the round was already over while the seat was
                // away. The resumed session never held a stream, so its own query legs
                // pump their own short loops — read, leave, and end the session for good.
                await ReadBoardAsync(resumed, report, options, cancellationToken);
                await resumed.LeaveRoundAsync(report.RoomId,
                    Bounded(cancellationToken, options.StepBudget));
                await resumed.LogoutAsync(Bounded(cancellationToken, options.StepBudget));
            }
            report.AddLeg("logged-out");
            return report;
        }

        /// <summary>The board reads: the projection follows the round's settlement, so
        /// the sample polls the first page until it answers — then the lead's position,
        /// the window around it, the cursor's second page, and the honest absent word
        /// for the session's own subject (a faction board scores factions, not players;
        /// Found false is the board's word, not an error).</summary>
        private static async Task ReadBoardAsync(ChorusSession session, SampleReport report,
            SampleOptions options, CancellationToken cancellationToken)
        {
            var key = new LeaderboardKey
            {
                BoardId = options.BoardId,
                ScopeId = report.RoomId,
                Subject = LeaderboardSubject.Faction,
            };
            LeaderboardTop? top = null;
            var settle = DateTime.UtcNow + options.BoardSettleBudget;
            while (top is null || top.Entries.Count == 0)
            {
                if (DateTime.UtcNow > settle) break;
                top = await session.GetTopAsync(key, 10, null,
                    Bounded(cancellationToken, options.StepBudget));
                if (top.Entries.Count == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            if (top is not null)
                report.TopEntries = top.Entries.Count;
            if (top is { } && top.Entries.Count > 0)
            {
                report.LeadScore = top.Entries[0].Score;
                var lead = await session.GetRankAsync(key, top.Entries[0].SubjectId,
                    Bounded(cancellationToken, options.StepBudget));
                report.LeadRankFound = lead.Found;
                report.LeadRank = lead.Rank;
                var around = await session.GetAroundMeAsync(key, top.Entries[0].SubjectId, 3,
                    Bounded(cancellationToken, options.StepBudget));
                report.AroundEntries = around.Entries.Count;
                var pageOne = await session.GetTopAsync(key, 1, null,
                    Bounded(cancellationToken, options.StepBudget));
                if (pageOne.HasMore && pageOne.NextCursor is not null)
                {
                    var pageTwo = await session.GetTopAsync(key, 1, pageOne.NextCursor,
                        Bounded(cancellationToken, options.StepBudget));
                    report.CursorPageEntries = pageTwo.Entries.Count;
                }
            }
            report.AddLeg("board:" + report.TopEntries);
            var own = await session.GetRankAsync(key, null,
                Bounded(cancellationToken, options.StepBudget));
            report.OwnRankFound = own.Found;
            report.AddLeg("reads:done");
        }

        /// <summary>One bounded wait for a word: polls while the pump advances the
        /// applier, answers false when the budget ends — the stuck word goes on the
        /// story, the sample walks on with what it has.</summary>
        private static async Task<bool> WaitForAsync(Func<bool> word, TimeSpan budget,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + budget;
            while (!word())
            {
                if (DateTime.UtcNow > deadline) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
            return true;
        }

        /// <summary>The pump: the downlink's only reader while the stream is live — every
        /// receipt and read answer rides it, so the pump runs for the stream's whole
        /// life and ends only on the cancel, the subscription's own close or the socket's
        /// words. It feeds the applier (snapshots, deltas, the frozen result) and counts
        /// the frames; a park or a resync order goes on the story as the game's own word
        /// (the applier never sees either) and the pump keeps reading — the exit legs
        /// still need their answers carried.</summary>
        private static async Task PumpAsync(ISyncStream stream, SyncApplier applier,
            SampleReport report, FrameTally tally, CancellationToken cancellationToken)
        {
            try
            {
                while (await stream.MoveNextAsync(cancellationToken))
                {
                    var frame = stream.Current;
                    if (frame.Kind == SyncFrameKind.Snapshot)
                    {
                        applier.Decide(frame);
                        tally.Snapshots++;
                    }
                    else if (frame.Kind == SyncFrameKind.Delta)
                    {
                        applier.Decide(frame);
                        tally.Deltas++;
                    }
                    else if (frame.Kind == SyncFrameKind.RoundResult)
                    {
                        applier.Decide(frame);
                        report.RoundEnded = true;
                        report.RoundResultJson = frame.RoundResult is null
                            ? string.Empty : frame.RoundResult.ResultJson;
                        report.AddLeg("round-result");
                    }
                    else if (frame.Kind == SyncFrameKind.Suspended)
                    {
                        report.AddLeg("suspended:" + (frame.Suspended is null
                            ? "?" : frame.Suspended.Reason));
                    }
                    else if (frame.Kind == SyncFrameKind.ResyncRequired)
                    {
                        report.AddLeg("resync-required:" +
                            (frame.ResyncRequired is null
                                ? "?" : frame.ResyncRequired.CurrentEpoch.ToString()));
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException
                || cancellationToken.IsCancellationRequested)
            {
                // The socket's own words on the way down — the drop, the cancel. The
                // story keeps what the pump saw.
            }
        }

        private static async Task SwallowAsync(Task pump)
        {
            try
            {
                await pump;
            }
            catch
            {
                // The pump's end is the socket's word; the report keeps what it saw.
            }
        }

        /// <summary>One bounded wait: the caller's token plus the budget, so a stuck link
        /// surfaces as the wait's own word, never a hung game.</summary>
        private static CancellationToken Bounded(CancellationToken cancellationToken, TimeSpan budget)
        {
            if (budget <= TimeSpan.Zero) budget = TimeSpan.FromMilliseconds(500);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(budget);
            return linked.Token;
        }
    }
}

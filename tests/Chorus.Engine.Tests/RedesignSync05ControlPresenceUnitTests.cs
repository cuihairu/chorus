using Chorus.GameServer;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-05 acceptance (判断全员控制会话离线): the server judges all-offline from the
/// round's control participants at their current connection generation, the server heartbeat
/// timeout and the round's declared grace (authoritative-sync.md:42) — an observer
/// disconnect, a zero-observer room, an old-generation late close or a single control
/// drop can never be misjudged as all-offline, and the grace only takes effect once it has
/// actually elapsed (and restarts on any reconnect, never carries over).
/// </summary>
public sealed class RedesignSync05ControlPresenceUnitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static ControlPresenceTracker Tracker(TimeSpan? timeout = null, TimeSpan? grace = null) =>
        new(timeout ?? Timeout, grace ?? Grace);

    [Fact]
    public void AnObserverDisconnectNeverMakesTheRoomLookAllOffline()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a", "host-b"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Connect(1, "host-b", PresenceLinkKind.Control, T0);
        tracker.Connect(1, "viewer-1", PresenceLinkKind.Observer, T0);

        tracker.Disconnect(1, "viewer-1", ControlLinkReason.Disconnected, T0.AddSeconds(1));

        var sample = tracker.Sample(T0.AddSeconds(2));
        Assert.Equal(ControlPresenceVerdict.Mixed, sample.Verdict);
        Assert.Equal(2, sample.Online);
    }

    [Fact]
    public void AZeroObserverRoomIsNotAllOfflineWhileAControlSessionIsLive()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a", "host-b"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Connect(1, "host-b", PresenceLinkKind.Control, T0);

        var sample = tracker.Sample(T0.AddSeconds(2));
        Assert.Equal(2, sample.Online);
        Assert.Equal(0, sample.Offline);
        Assert.Equal(ControlPresenceVerdict.Mixed, sample.Verdict);
    }

    [Fact]
    public void AnOldGenerationLateCloseIsIgnored()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);       // first session
        tracker.Connect(2, "host-a", PresenceLinkKind.Control, T0.AddSeconds(1)); // reconnected
        // A late close from the superseded generation 1 must not take the live gen-2 down.
        tracker.Disconnect(1, "host-a", ControlLinkReason.Disconnected, T0.AddSeconds(2));

        var sample = tracker.Sample(T0.AddSeconds(3));
        Assert.True(sample.Participants[0].Online);
        Assert.Equal(ControlPresenceVerdict.Mixed, sample.Verdict);
    }

    [Fact]
    public void AnOldGenerationConnectDoesNotResurrectANewerOfflineConnection()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Connect(2, "host-a", PresenceLinkKind.Control, T0.AddSeconds(1));
        tracker.Disconnect(2, "host-a", ControlLinkReason.Exited, T0.AddSeconds(2));
        // A stale connect at generation 1 (already superseded by 2) must not mark it online.
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0.AddSeconds(3));

        var sample = tracker.Sample(T0.AddSeconds(4));
        Assert.False(sample.Participants[0].Online);
        Assert.Equal(ControlLinkReason.Exited, sample.Participants[0].Reason);
        Assert.Equal(ControlPresenceVerdict.AllOfflineWithinGrace, sample.Verdict);
    }

    [Fact]
    public void ASingleEndDropIsMixedNotAllOffline()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a", "host-b"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Connect(1, "host-b", PresenceLinkKind.Control, T0);

        tracker.Disconnect(1, "host-b", ControlLinkReason.Disconnected, T0.AddSeconds(1));

        var sample = tracker.Sample(T0.AddSeconds(2));
        Assert.Equal(1, sample.Online);
        Assert.Equal(1, sample.Offline);
        Assert.Equal(ControlPresenceVerdict.Mixed, sample.Verdict);
    }

    [Fact]
    public void AllOfflineBeforeTheGraceIsWithinGrace()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a", "host-b"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Connect(1, "host-b", PresenceLinkKind.Control, T0);
        tracker.Disconnect(1, "host-a", ControlLinkReason.Disconnected, T0.AddSeconds(1));
        tracker.Disconnect(1, "host-b", ControlLinkReason.Disconnected, T0.AddSeconds(1));

        var sample = tracker.Sample(T0.AddSeconds(1 + (int)Grace.TotalSeconds - 1));
        Assert.Equal(ControlPresenceVerdict.AllOfflineWithinGrace, sample.Verdict);
        Assert.Equal(0, sample.Online);
        Assert.Equal(2, sample.Offline);
    }

    [Fact]
    public void AllOfflineAtTheGraceBoundaryIsElapsed()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a", "host-b"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Connect(1, "host-b", PresenceLinkKind.Control, T0);
        tracker.Disconnect(1, "host-a", ControlLinkReason.Disconnected, T0);
        tracker.Disconnect(1, "host-b", ControlLinkReason.Disconnected, T0);

        var sample = tracker.Sample(T0.Add(Grace));
        Assert.Equal(ControlPresenceVerdict.AllOfflineGraceElapsed, sample.Verdict);
        Assert.Equal(Grace, sample.AllOfflineFor);
    }

    [Fact]
    public void AReconnectWithinGraceResetsTheRun()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a", "host-b"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Connect(1, "host-b", PresenceLinkKind.Control, T0);
        tracker.Disconnect(1, "host-a", ControlLinkReason.Disconnected, T0);
        tracker.Disconnect(1, "host-b", ControlLinkReason.Disconnected, T0);

        var within = tracker.Sample(T0.AddSeconds(5));
        Assert.Equal(ControlPresenceVerdict.AllOfflineWithinGrace, within.Verdict);

        // A reconnect inside the grace restores one control session: no longer all-offline.
        tracker.Connect(2, "host-b", PresenceLinkKind.Control, T0.AddSeconds(5));
        Assert.Equal(ControlPresenceVerdict.Mixed, tracker.Sample(T0.AddSeconds(6)).Verdict);

        // Dropping again restarts the window from the new offline instant, so the grace
        // does not carry the earlier run across the interruption.
        tracker.Disconnect(2, "host-b", ControlLinkReason.Disconnected, T0.AddSeconds(6));
        var restarted = tracker.Sample(T0.AddSeconds(6 + 5));
        Assert.Equal(ControlPresenceVerdict.AllOfflineWithinGrace, restarted.Verdict);
        Assert.Equal(TimeSpan.FromSeconds(5), restarted.AllOfflineFor);
    }

    [Fact]
    public void TheServerTimeoutJudgesASilentConnectionOffline()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);

        Assert.True(tracker.Sample(T0.Add(Timeout) - TimeSpan.FromTicks(1)).Participants[0].Online);
        var timedOut = tracker.Sample(T0.Add(Timeout));
        Assert.False(timedOut.Participants[0].Online);
        Assert.Equal(ControlLinkReason.TimedOut, timedOut.Participants[0].Reason);
        Assert.Equal(ControlPresenceVerdict.AllOfflineWithinGrace, timedOut.Verdict);
    }

    [Fact]
    public void AHeartbeatKeepsTheConnectionLive()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Heartbeat(1, "host-a", T0.Add(Timeout) - TimeSpan.FromSeconds(1));

        var sample = tracker.Sample(T0.Add(TimeSpan.FromTicks(Timeout.Ticks * 2) - TimeSpan.FromSeconds(2)));
        Assert.True(sample.Participants[0].Online);
        Assert.Equal(ControlPresenceVerdict.Mixed, sample.Verdict);
    }

    [Fact]
    public void AStaleHeartbeatNeverResurrectsAnOfflineConnection()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Disconnect(1, "host-a", ControlLinkReason.Banned, T0.AddSeconds(1));
        tracker.Heartbeat(1, "host-a", T0.AddSeconds(2)); // offline: heartbeat is a no-op

        var sample = tracker.Sample(T0.AddSeconds(3));
        Assert.False(sample.Participants[0].Online);
        Assert.Equal(ControlLinkReason.Banned, sample.Participants[0].Reason);
    }

    [Fact]
    public void EachTerminalReasonIsDistinguished()
    {
        var reasons = new[]
        {
            ControlLinkReason.Disconnected, ControlLinkReason.BroadcastClosed,
            ControlLinkReason.Exited, ControlLinkReason.Banned, ControlLinkReason.ServiceFailure
        };
        foreach (var reason in reasons)
        {
            var tracker = Tracker();
            tracker.SeedControlParticipants(["host-a"], T0);
            tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
            tracker.Disconnect(1, "host-a", reason, T0.AddSeconds(1));

            var sample = tracker.Sample(T0.AddSeconds(2));
            Assert.False(sample.Participants[0].Online);
            Assert.Equal(reason, sample.Participants[0].Reason);
        }
    }

    [Fact]
    public void AnUnlinkedControlParticipantIsOfflineButKeepsMixedWhenAnotherIsOnline()
    {
        // A control participant with no current valid control session is offline — but a
        // single live session keeps the round Mixed, so a startup that has not yet linked
        // every host is never misjudged as all-offline.
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a", "host-b"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);

        var sample = tracker.Sample(T0.AddSeconds(2));
        Assert.Equal(1, sample.Online);
        Assert.Equal(1, sample.Offline);
        Assert.Equal(ControlPresenceVerdict.Mixed, sample.Verdict);
    }

    [Fact]
    public void TheOfflineRunIsMeasuredFromTheLastParticipantToGoOffline()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a", "host-b"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Connect(1, "host-b", PresenceLinkKind.Control, T0);
        tracker.Disconnect(1, "host-a", ControlLinkReason.Disconnected, T0);      // all-offline from here? no — b live
        tracker.Disconnect(1, "host-b", ControlLinkReason.Disconnected, T0.AddSeconds(3)); // last to go

        // 3s after B dropped: A has been offline longer, but the run is from B's drop.
        var sample = tracker.Sample(T0.AddSeconds(3 + 3));
        Assert.Equal(TimeSpan.FromSeconds(3), sample.AllOfflineFor);
        Assert.Equal(ControlPresenceVerdict.AllOfflineWithinGrace, sample.Verdict);
    }

    [Fact]
    public void AnEmptyControlSetIsMixedNeverAllOffline()
    {
        var tracker = Tracker();
        var sample = tracker.Sample(T0.AddSeconds(100));
        Assert.Equal(0, sample.ControlParticipants);
        Assert.Equal(ControlPresenceVerdict.Mixed, sample.Verdict);
    }

    [Fact]
    public void AnUnknownIdentityReportIsIgnored()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        tracker.Disconnect(1, "stranger", ControlLinkReason.Disconnected, T0.AddSeconds(1));

        var sample = tracker.Sample(T0.AddSeconds(2));
        Assert.Equal(1, sample.ControlParticipants);
        Assert.Equal(1, sample.Online);
        Assert.Equal(ControlPresenceVerdict.Mixed, sample.Verdict);
    }

    [Fact]
    public void TheConstructorRejectsANonPositiveTimeoutOrGrace()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPresenceTracker(TimeSpan.Zero, Grace));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPresenceTracker(-Timeout, Grace));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPresenceTracker(Timeout, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPresenceTracker(Timeout, -Grace));
    }

    [Fact]
    public void ADisconnectReportNeverCarriesATerminalSynthesizedReason()
    {
        var tracker = Tracker();
        tracker.SeedControlParticipants(["host-a"], T0);
        tracker.Connect(1, "host-a", PresenceLinkKind.Control, T0);
        // TimedOut is synthesized by Sample, never a caller report.
        Assert.Throws<ArgumentException>(
            () => tracker.Disconnect(1, "host-a", ControlLinkReason.TimedOut, T0));
    }
}

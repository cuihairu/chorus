using System.Diagnostics;

namespace Chorus.SimulatorPlatform;

/// <summary>The runner's standing totals for the panel (BOTS-2 reads these through the
/// fleet's metrics face): when it started and finished, how many events the script
/// scheduled, and the poster's own outcome counts.</summary>
public sealed record FleetSnapshot(
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, long Scheduled,
    long Posted, PosterSnapshot Poster);

/// <summary>
/// Plays one bot script's timeline in real time: every scheduled event goes out at its
/// absolute offset from the run's start (the same absolute-schedule rule the load tool's
/// pacer uses — a slow post shifts a pulse's latency, never its count or the schedule's
/// rate). Events ride the ingress face through the poster; the runner owns no transport
/// and no identity of its own.
/// </summary>
public sealed class BotFleetRunner
{
    private readonly BotScript _script;
    private readonly AudienceEventPoster _poster;

    public BotFleetRunner(BotScript script, AudienceEventPoster poster)
    {
        _script = script;
        _poster = poster;
    }

    public BotScript Script => _script;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public bool Completed => FinishedAt is not null;

    /// <summary>Plays the whole schedule once and returns when the last event's post is
    /// answered. Parallel fleet runs are separate runners sharing one id sequence.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (StartedAt is not null)
            throw new InvalidOperationException("A fleet runner plays its script once.");
        StartedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        foreach (var evt in _script.Schedule)
        {
            var due = TimeSpan.FromSeconds(evt.OffsetSeconds) - clock.Elapsed;
            if (due > TimeSpan.Zero)
                await Task.Delay(due, cancellationToken).ConfigureAwait(false);
            await _poster.PostAsync(evt, cancellationToken).ConfigureAwait(false);
        }
        FinishedAt = DateTimeOffset.UtcNow;
    }

    public FleetSnapshot Snapshot()
    {
        var poster = _poster.Snapshot();
        return new FleetSnapshot(StartedAt, FinishedAt, _script.Schedule.Count,
            poster.SentByKind.Values.Sum(), poster);
    }
}

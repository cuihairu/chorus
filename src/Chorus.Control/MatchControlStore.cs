namespace Chorus.Control;

public sealed partial class RoomControlStore
{
    public MatchRules Rules() => database.Read(t => t.Get<MatchRules>("system", "rules") ?? new());
    public MatchRules SetRules(MatchRules request, string actor) => database.Write(t =>
    {
        var old = t.Get<MatchRules>("system", "rules") ?? new();
        if (old.Version != request.Version) throw new ControlConflictException("Rules version changed; refresh preview.");
        if (request.TierWidth < 1 || request.MaxRatingGap < 0 || request.WidenAfterMs < 0 || request.TimeoutMs <= request.WidenAfterMs
            || request.LevelExperience < 1 || request.StarExperience < 1 || request.RematchCooldownMs < 0 || string.IsNullOrWhiteSpace(request.Season))
            throw new ArgumentException("Invalid matchmaking/growth thresholds.");
        if (request.FillBots) throw new ArgumentException("Control-plane bot matches require a game-runtime adapter; use real-player matching.");
        var rules = request with { Version = old.Version + 1 };
        t.Put("system", "rules", rules); t.Audit(Now, actor, "rules.update", "matchmaking", new { before = old, after = rules });
        return rules;
    });
    public List<StreamerProfile> Streamers() => database.Read(t => t.List<StreamerProfile>("streamer"));
    public StreamerProfile SaveProfile(StreamerProfile profile, string actor) => database.Write(t =>
    {
        Id(profile.Id); Id(profile.AccountId); Id(profile.Platform);
        if (profile.Experience < 0 || profile.Influence < 0 || profile.Rating < 0 || profile.Games < 0 || profile.Wins < 0 || profile.Wins > profile.Games)
            throw new ArgumentException("Invalid profile totals.");
        var old = t.Get<StreamerProfile>("streamer", profile.Id);
        if (old is not null && old.Version != profile.Version) throw new ControlConflictException("Profile version changed.");
        if (t.List<StreamerProfile>("streamer").Any(s => s.Id != profile.Id && s.Platform == profile.Platform && s.AccountId == profile.AccountId))
            throw new ControlConflictException("Platform account belongs to another streamer.");
        var result = profile with { Version = (old?.Version ?? 0) + 1, UpdatedAtMs = Now };
        t.Put("streamer", profile.Id, result); t.Audit(Now, actor, "streamer.update", profile.Id, new { before = old, after = result });
        return result;
    });
    public List<QueueEntry> Queue() => database.Read(t => t.List<QueueEntry>("queue"));
    public List<MatchRecord> Matches() => database.Read(t => t.List<MatchRecord>("match"));
    public QueueEntry Join(string streamerId, MatchJoin request, string actor) => database.Write(t =>
    {
        Id(request.Mode); Id(request.Region);
        var profile = t.Get<StreamerProfile>("streamer", streamerId) ?? throw new KeyNotFoundException("Streamer profile required.");
        if (!Connections(t).Any(c => c.Role == "streamer" && c.UserId == streamerId)) throw new ControlConflictException("Streamer must be online to queue.");
        if (t.List<MatchRecord>("match").Any(m => m.Status is "AwaitingReady" or "Running" && m.StreamerIds.Contains(streamerId))) throw new ControlConflictException("Streamer already in a match.");
        var old = t.Get<QueueEntry>("queue", streamerId);
        if (old is not null) return old;
        var entry = new QueueEntry(streamerId, request.Mode, request.Region, profile.Platform, Now);
        t.Put("queue", streamerId, entry); t.Audit(Now, actor, "match.join", streamerId, request);
        return entry;
    });
    public bool Leave(string id, string actor) => database.Write(t =>
    {
        var old = t.Get<QueueEntry>("queue", id);
        if (old is null) return false;
        t.Delete("queue", id); t.Audit(Now, actor, "match.cancel", id, old);
        return true;
    });
    public List<MatchRecord> FormMatches() => database.Write(t =>
    {
        var rules = t.Get<MatchRules>("system", "rules") ?? new();
        var online = Connections(t).Where(c => c.Role == "streamer").Select(c => c.UserId).ToHashSet(StringComparer.Ordinal);
        var queue = t.List<QueueEntry>("queue").OrderBy(q => q.JoinedAtMs).ThenBy(q => q.StreamerId, StringComparer.Ordinal).ToList();
        var profiles = t.List<StreamerProfile>("streamer").ToDictionary(p => p.Id, StringComparer.Ordinal);
        var now = Now;
        var recentOpponents = t.List<MatchRecord>("match")
            .Where(m => m.Status != "Cancelled" && now - (m.FinishedAtMs ?? m.CreatedAtMs) < rules.RematchCooldownMs)
            .SelectMany(m => m.StreamerIds.SelectMany(a => m.StreamerIds.Where(b => b != a).Select(b => (a, b))))
            .ToHashSet();
        List<MatchRecord> created = [];
        foreach (var first in queue.ToArray())
        {
            if (!queue.Contains(first)) continue;
            if (!online.Contains(first.StreamerId)) { t.Delete("queue", first.StreamerId); queue.Remove(first); t.Audit(Now,"system","match.offline",first.StreamerId,first); continue; }
            var a = profiles[first.StreamerId];
            var waited = Now - first.JoinedAtMs;
            var second = queue.Where(q => q != first && online.Contains(q.StreamerId) && q.Mode == first.Mode && q.Region == first.Region && q.Platform == first.Platform)
                .Where(q =>
                {
                    var b = profiles[q.StreamerId];
                    var sameTier = a.Rating / rules.TierWidth == b.Rating / rules.TierWidth;
                    return Math.Abs(a.Rating - b.Rating) <= rules.MaxRatingGap
                        && (sameTier || waited >= rules.WidenAfterMs && Now - q.JoinedAtMs >= rules.WidenAfterMs)
                        && !recentOpponents.Contains((a.Id, b.Id));
                }).OrderBy(q => Math.Abs(a.Rating - profiles[q.StreamerId].Rating)).ThenBy(q => q.JoinedAtMs).FirstOrDefault();
            // No bot runtime adapter is wired into this host: timeout cannot fabricate a match.
            if (second is null)
            {
                if (waited >= rules.TimeoutMs) { t.Delete("queue", first.StreamerId); queue.Remove(first); t.Audit(Now,"system","match.timeout",first.StreamerId,first); }
                continue;
            }
            var id = Guid.NewGuid().ToString("N");
            var ids = new[] { first.StreamerId, second.StreamerId };
            var roomId = $"match-{id}";
            // Assignment and queue consumption share one transaction. Capacity failure leaves both queued.
            RoomRecord room;
            try { room = Assign(t, new(roomId, first.StreamerId, first.Platform, first.Mode, first.Region)); }
            catch (ControlConflictException) { continue; }
            t.Put("room", roomId, room with { MatchId = id });
            var gap = Math.Abs(a.Rating - profiles[second.StreamerId].Rating);
            var match = new MatchRecord(id, first.Mode, first.Region, rules.Season, rules.Version, ids, [roomId], Now, gap,
                a.Rating / rules.TierWidth != profiles[second.StreamerId].Rating / rules.TierWidth, false, Status: "AwaitingReady",
                ReadyDeadlineMs: now + 120_000, QueueEntries: queue.Where(q => ids.Contains(q.StreamerId)).ToArray());
            t.Put("match", id, match); created.Add(match);
            foreach (var player in ids) { t.Delete("queue", player); queue.RemoveAll(q => q.StreamerId == player); }
            t.Audit(Now,"system","match.formed",id,new { match, reason = "Compatible tier/rating, mode, region and platform" });
        }
        return created;
    });
    public MatchRecord Finish(string id, MatchFinish result, string actor) => database.Write(t =>
    {
        Id(result.SourceId);
        var match = t.Get<MatchRecord>("match", id) ?? throw new KeyNotFoundException(id);
        if (result.WinnerId is not null && !match.StreamerIds.Contains(result.WinnerId)) throw new ArgumentException("Winner is not a match participant.");
        if (match.Status == "Finished")
        {
            if (match.WinnerId != result.WinnerId) throw new ControlConflictException("Result already settled differently.");
            return match;
        }
        if (match.Status != "Running") throw new ControlConflictException("Only a running match can settle.");
        match = match with { Status = "Finished", WinnerId = result.WinnerId, FinishedAtMs = Now };
        t.Put("match", id, match);
        foreach (var player in match.StreamerIds)
        {
            var p = t.Get<StreamerProfile>("streamer", player)!;
            var won = player == result.WinnerId;
            var ratingDelta = result.WinnerId is null ? 0 : won ? 20 : -20;
            p = p with { Games = p.Games + 1, Wins = p.Wins + (won ? 1 : 0), Experience = p.Experience + (won ? 20 : 10), Rating = Math.Max(0, p.Rating + ratingDelta), Version = p.Version + 1, UpdatedAtMs = Now };
            t.Put("streamer", player, p);
            foreach (var scope in new[] { "match", "period", "global" })
            {
                var scopeId = scope == "match" ? id : scope == "period" ? match.Season : "all";
                ChangeScore(t, $"{scope}:{scopeId}", scope, match.Season, new(player, won ? 1 : 0, $"match:{id}:{player}", "wins"));
            }
        }
        var board = t.Get<StoredBoard>("board", $"match:{id}");
        if (board is not null) t.Put("board", board.Id, board with { Frozen = true });
        RetireMatchRooms(t, match);
        t.Audit(Now, actor, "match.finish", id, result);
        return match;
    });
    public List<StoredBoard> Boards() => database.Read(t => t.List<StoredBoard>("board"));
    private StoredBoard ChangeScore(ControlDatabase.Transaction t, string id, string scope, string season, ScoreChange change)
    {
        Id(id); Id(change.ParticipantId); Id(change.SourceId);
        var board = t.GetForUpdate<StoredBoard>("board", id) ?? new(id, scope, change.Unit, season, new Dictionary<string,long>(), new HashSet<string>(), Now);
        if (board.Sources.Contains(change.SourceId) || !t.TryRecordBoardSource(id, change.SourceId)) return board;
        if (board.Frozen) throw new ControlConflictException("Leaderboard is frozen.");
        if (board.Unit != change.Unit) throw new ControlConflictException("Leaderboard units differ.");
        // Legacy embedded receipts remain readable; new receipts use the indexed table.
        var scores = new Dictionary<string,long>(board.Scores);
        scores[change.ParticipantId] = checked(scores.GetValueOrDefault(change.ParticipantId) + change.Delta);
        board = board with { Scores = scores, UpdatedAtMs = Now };
        t.Put("board", id, board); return board;
    }
    public StoredBoard ChangeScore(string id, string scope, string season, ScoreChange change, string actor) => database.Write(t =>
    {
        if (scope is not ("room" or "match" or "period" or "global" or "platform")) throw new ArgumentException("Unknown leaderboard scope.");
        var board = ChangeScore(t, id, scope, season, change);
        t.Audit(Now,actor,"board.score",id,change); return board;
    });
    public StoredBoard Freeze(string id, string actor) => database.Write(t =>
    {
        var board = t.Get<StoredBoard>("board", id) ?? throw new KeyNotFoundException(id);
        if (!board.Frozen) { board = board with { Frozen = true, UpdatedAtMs = Now }; t.Put("board", id, board); t.Audit(Now,actor,"board.freeze",id,new { board.UpdatedAtMs }); }
        return board;
    });
}

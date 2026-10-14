using System.Text.Json;
using Chorus.Engine.Abstractions;
using Chorus.Protocol;
using Chorus.Protocol.Models;

namespace Chorus.SampleGame;

public sealed class SampleGameModule : IGameModule
{
    private readonly string _sessionId;
    private readonly MatchStateStore _matchStore;
    private long _tickId;

    private readonly Dictionary<string, long> _likesByUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _giftsByUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _commentsByUser = new(StringComparer.Ordinal);
    private string? _matchId;

    public SampleGameModule(string sessionId)
        : this(sessionId, new MatchStateStore())
    {
    }

    internal SampleGameModule(string sessionId, MatchStateStore matchStore)
    {
        _sessionId = sessionId;
        _matchStore = matchStore;
    }

    public string Name => "sample";

    public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
    {
        _tickId = tickId;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case AudienceEventKind.Like:
                    {
                        var inc = 1;
                        if (!string.IsNullOrWhiteSpace(e.Text) && long.TryParse(e.Text, out var parsed))
                        {
                            inc = (int)parsed;
                        }
                        _likesByUser[e.UserId] = _likesByUser.TryGetValue(e.UserId, out var likeTotal) ? likeTotal + inc : inc;
                        TrackMatchContext(e, likes: inc, gifts: 0, comments: 0);
                    }
                    break;
                case AudienceEventKind.Gift:
                    {
                        var inc = e.GiftCount ?? 1;
                        _giftsByUser[e.UserId] = _giftsByUser.TryGetValue(e.UserId, out var giftTotal) ? giftTotal + inc : inc;
                        TrackMatchContext(e, likes: 0, gifts: inc, comments: 0);
                    }
                    break;
                case AudienceEventKind.Comment:
                    _commentsByUser[e.UserId] = _commentsByUser.TryGetValue(e.UserId, out var commentTotal) ? commentTotal + 1 : 1;
                    TrackMatchContext(e, likes: 0, gifts: 0, comments: 1);
                    break;
            }
        }
    }

    private void TrackMatchContext(AudienceEvent e, int likes, int gifts, int comments)
    {
        if (!string.IsNullOrWhiteSpace(e.MatchId))
        {
            _matchId ??= e.MatchId;
        }

        _matchStore.Apply(_sessionId, e, likes, gifts, comments);
    }

    public object GetStateSnapshot()
    {
        var match = _matchStore.GetSnapshot(_matchId);

        return new
        {
            game = Name,
            sessionId = _sessionId,
            match,
            matchId = _matchId,
            tickId = _tickId,
            // Full per-user maps: the restore contract round-trips through
            // this snapshot (totals/top below are display projections).
            users = new
            {
                likes = _likesByUser,
                gifts = _giftsByUser,
                comments = _commentsByUser,
            },
            totals = new
            {
                users = _likesByUser.Keys.Union(_giftsByUser.Keys).Union(_commentsByUser.Keys).Distinct().Count(),
                likes = _likesByUser.Values.Sum(),
                gifts = _giftsByUser.Values.Sum(),
                comments = _commentsByUser.Values.Sum(),
            },
            top = new
            {
                likes = _likesByUser
                    .OrderByDescending(kv => kv.Value)
                    .Take(10)
                    .Select(kv => new { userId = kv.Key, value = kv.Value })
                    .ToArray(),
                gifts = _giftsByUser
                    .OrderByDescending(kv => kv.Value)
                    .Take(10)
                    .Select(kv => new { userId = kv.Key, value = kv.Value })
                    .ToArray(),
            }
        };
    }

    /// <summary>
    /// Restores session-local state (tick, match id, per-user counters) from
    /// the snapshot produced by <see cref="GetStateSnapshot"/>. The <c>match</c>
    /// section is a cross-session store projection and deliberately not part
    /// of the per-session restore contract (it rebuilds as new events arrive).
    /// Anything unrecognized throws - the session then refuses to start
    /// (fail-closed, snapshot-restore.md 方案 A).
    /// </summary>
    public void RestoreState(long version, JsonElement state)
    {
        if (version != 1)
        {
            throw new InvalidOperationException($"sample module supports snapshot version 1 only, got {version}");
        }

        RestorableState? restored;
        try
        {
            restored = state.Deserialize<RestorableState>(ProtocolJson.SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("malformed sample snapshot state", ex);
        }

        if (restored?.Users is null)
        {
            throw new InvalidOperationException("sample snapshot state is missing the users section");
        }

        _tickId = restored.TickId;
        _matchId = restored.MatchId;
        _likesByUser.Clear();
        _giftsByUser.Clear();
        _commentsByUser.Clear();
        foreach (var (userId, likes) in restored.Users.Likes ?? new())
        {
            _likesByUser[userId] = likes;
        }

        foreach (var (userId, gifts) in restored.Users.Gifts ?? new())
        {
            _giftsByUser[userId] = gifts;
        }

        foreach (var (userId, comments) in restored.Users.Comments ?? new())
        {
            _commentsByUser[userId] = comments;
        }
    }

    private sealed record RestorableState(long TickId, string? MatchId, RestorableUsers? Users);

    private sealed record RestorableUsers(
        Dictionary<string, long>? Likes,
        Dictionary<string, long>? Gifts,
        Dictionary<string, long>? Comments);

    public void Dispose()
    {
    }
}

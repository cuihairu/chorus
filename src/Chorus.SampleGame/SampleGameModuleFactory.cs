using Chorus.Engine.Abstractions;

namespace Chorus.SampleGame;

/// <summary>
/// One shared <see cref="MatchStateStore"/> across all sessions it creates: the
/// match view is a cross-session aggregation (audit §4). An optional
/// <see cref="ISessionScoreSink"/> turns the modules into authoritative room
/// board writers (docs/guide/leaderboard.md); without it the modules stay
/// projection-free and nothing changes for the current hosts.
/// </summary>
public sealed class SampleGameModuleFactory : IGameModuleFactory
{
    private readonly MatchStateStore _matchStore = new();
    private readonly ISessionScoreSink? _scoreSink;

    public SampleGameModuleFactory(ISessionScoreSink? scoreSink = null)
    {
        _scoreSink = scoreSink;
    }

    public IGameModule Create(string sessionId) => new SampleGameModule(sessionId, _matchStore, _scoreSink);
}

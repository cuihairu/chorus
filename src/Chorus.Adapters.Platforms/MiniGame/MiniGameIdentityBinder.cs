using Chorus.Engine.Routing;

namespace Chorus.Adapters.Platforms.MiniGame;

/// <summary>
/// Outcome of binding a mini-game login session to a Chorus identity.
/// <param name="Mapped">True when the session is bound to an identity.</param>
/// <param name="Identity">The bound identity (the existing one on a conflict).</param>
/// <param name="Conflicting">
/// Non-null when the platform account is already bound to a *different* Chorus
/// identity - the mapping is never silently overwritten (fail visible).
/// </param>
/// </summary>
public sealed record MiniGameBindResult(bool Mapped, PlatformIdentity Identity, PlatformIdentity? Conflicting);

/// <summary>
/// Binds a mini-game login session to a Chorus player identity via
/// <see cref="PlatformIdentityRegistry"/> (todo §14 identity mapping, §17
/// mini-game 身份映射 half). The Chorus identity id is derived deterministically
/// from (platform, platform account id) by default so a re-login is an
/// idempotent re-bind; inject a factory to delegate to the real identity store
/// when the control-plane wiring lands.
///
/// Mapping conflicts are fail-visible: a platform account already bound to a
/// different Chorus identity is reported back to the caller instead of being
/// re-pointed, so a stolen login code can never silently hijack another player.
/// </summary>
public sealed class MiniGameIdentityBinder
{
    private readonly PlatformIdentityRegistry _identities;
    private readonly Func<string, string, string> _identityIdFactory;

    public MiniGameIdentityBinder(
        PlatformIdentityRegistry identities,
        Func<string, string, string>? identityIdFactory = null)
    {
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _identityIdFactory = identityIdFactory ?? DefaultIdentityId;
    }

    /// <summary>
    /// Default deterministic identity id: "<platform>:<platform account id>".
    /// Deterministic so a repeated login binds to the same player.
    /// </summary>
    private static string DefaultIdentityId(string platform, string platformAccountId)
        => $"{platform}:{platformAccountId}";

    /// <summary>
    /// Bind a login session. Null when the session is unusable (fail closed -
    /// the caller must reject the login instead of guessing an identity).
    /// </summary>
    public MiniGameBindResult? TryBind(MiniGameLoginSession? session)
    {
        if (session is null
            || string.IsNullOrWhiteSpace(session.Platform)
            || string.IsNullOrWhiteSpace(session.PlatformAccountId))
        {
            return null;
        }

        var identityId = _identityIdFactory(session.Platform, session.PlatformAccountId);
        if (string.IsNullOrWhiteSpace(identityId))
        {
            return null;
        }

        var identity = new PlatformIdentity(PlatformIdentityKind.Player, identityId.Trim());
        if (_identities.TryMap(session.Platform.Trim(), session.PlatformAccountId.Trim(), identity, out var existing))
        {
            return new MiniGameBindResult(Mapped: true, identity, Conflicting: null);
        }

        // 冲突：该平台账号已绑定到其他 Chorus 身份，不隐式覆盖。
        return new MiniGameBindResult(Mapped: false, identity, Conflicting: existing);
    }
}

using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;

namespace Chorus.StreamerServer;

public sealed record LoginCommand(
    string Platform, string AppId, string LaunchCredential, string LoginAttemptId,
    byte[] NextResumeSecret, string GatewayInstanceId, string ConnectionId);

public sealed record ResumeCommand(
    string PreviousSessionId, string ResumeOperationId, byte[] PreviousSecret, byte[] NextSecret,
    string GatewayInstanceId, string ConnectionId);

public sealed record AttachConnectionCommand(
    string SessionId, string OperationId, byte[] CurrentSecret, string GatewayInstanceId, string ConnectionId);

public sealed record StreamerLoginReady(
    string StreamerId, string SessionId, long SessionVersion, string AccessCredential,
    DateTimeOffset AccessExpiresAtUtc, bool Duplicate);

/// <summary>Session grant as seen from this side of the DataServer boundary.</summary>
public sealed record StreamerSessionGrant(
    string StreamerId, string SessionId, long SessionVersion, DateTime ExpiresAtUtc, bool Duplicate);

public sealed record StreamerConnection(
    string SessionId, string StreamerId, long SessionVersion, long ConnectionGeneration, bool Duplicate);

public sealed record StreamerSessionState(
    string StreamerId, string TenantId, long SessionVersion, string GatewayInstanceId, string ConnectionId,
    long ConnectionGeneration, bool Revoked, bool Closed, bool Expired, bool Banned, bool Current);

public sealed record StreamerAccessState(
    string StreamerId, string SessionId, long SessionVersion, string GatewayInstanceId, string ConnectionId,
    long ConnectionGeneration, bool Online);

public sealed record StreamerBinding(
    string BindingId, string LiveSessionId, string GameId, long Generation, string State, bool Duplicate);

public sealed record StreamerBindingStatus(
    string LiveSessionId, string BindingId, string GameId, long Generation, string State);

/// <summary>The entity is not usable; LoginReady must not be returned (AT01).</summary>
public sealed class EntityNotReadyException : Exception;

/// <summary>The caller's tenant is not allowed to use this application mapping.</summary>
public sealed class TenantApplicationDeniedException : Exception;

/// <summary>The access credential is not authentic, expired, or no longer the current session.</summary>
public sealed class AccessCredentialRejectedException : Exception;

/// <summary>Identity/session operations DataServer owns; DataServer enforces tenant fencing.</summary>
public interface IStreamerIdentityStore
{
    Task<string> ResolveIdentityAsync(string tenantId, string platform, string appId, string openId,
        CancellationToken cancellationToken = default);
    Task<StreamerSessionGrant> LoginAsync(string tenantId, string streamerId, string gatewayInstanceId,
        string connectionId, byte[] nextResumeSecret, CancellationToken cancellationToken = default);
    Task<StreamerSessionGrant> ResumeAsync(string tenantId, string previousSessionId, string gatewayInstanceId,
        string connectionId, string operationId, byte[] previousSecret, byte[] nextSecret,
        CancellationToken cancellationToken = default);
    Task<StreamerConnection> AttachConnectionAsync(string tenantId, string sessionId, string gatewayInstanceId,
        string connectionId, string operationId, byte[] currentSecret, CancellationToken cancellationToken = default);
    Task<bool> ConnectionClosedAsync(string tenantId, string sessionId, string gatewayInstanceId,
        string connectionId, long connectionGeneration, CancellationToken cancellationToken = default);
    Task<StreamerSessionState?> GetSessionStateAsync(string tenantId, string sessionId,
        CancellationToken cancellationToken = default);
    Task<string> OpenLiveSessionAsync(string tenantId, string streamerId, string platform, string appId,
        string platformRoomKey, string canonicalLiveSessionKey, CancellationToken cancellationToken = default);
    Task<string?> GetLiveSessionIdAsync(string tenantId, string streamerId,
        CancellationToken cancellationToken = default);
    Task<StreamerBinding> StartGameplayAsync(string tenantId, string liveSessionId, string sessionId,
        string gameId, string operationId, CancellationToken cancellationToken = default);
    Task<StreamerBindingStatus?> GetActiveBindingAsync(string tenantId, string streamerId,
        CancellationToken cancellationToken = default);
}

/// <summary>Entity profile loading owned by DataServer; login succeeds only after the load.</summary>
public interface IStreamerProfileStore
{
    Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Login/Resume orchestration per the redesign chapter 5: verify the platform launch, resolve the
/// tenant-scoped identity, load the entity profile, then take over or rotate the control session.
/// No LoginReady is returned unless the entity load succeeds.
/// </summary>
public sealed class RedesignStreamerLoginCoordinator(
    RedesignPlatformGateway platformGateway,
    IStreamerIdentityStore identityStore,
    IStreamerProfileStore profileStore,
    StreamerActorRegistry actorRegistry,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tenantApplications,
    byte[] accessCredentialKey,
    Func<DateTimeOffset>? clock = null,
    StreamerOwnershipRegistrar? ownershipRegistrar = null)
{
    public async Task<StreamerLoginReady> LoginAsync(string callerTenantId, LoginCommand command,
        CancellationToken cancellationToken = default)
    {
        var gameId = GameIdFor(callerTenantId, command.AppId);
        var verified = await platformGateway.VerifyLaunchAsync(command.Platform,
            new PlatformLaunchRequest(command.AppId, command.LaunchCredential, command.LoginAttemptId),
            cancellationToken);
        var streamerId = await identityStore.ResolveIdentityAsync(callerTenantId, verified.Platform,
            verified.AppId, verified.OpenId, cancellationToken);
        await EnsureEntityReadyAsync(callerTenantId, gameId, streamerId, cancellationToken);
        // The verified canonical live key opens (or reuses) the live session and its cross-GameId slot.
        await identityStore.OpenLiveSessionAsync(callerTenantId, streamerId, verified.Platform, verified.AppId,
            verified.CanonicalLiveSessionKey, verified.CanonicalLiveSessionKey, cancellationToken);
        // Chapter 5.1 step 5: load the entity (including the mount to restore) before committing the
        // session. A recovery failure means no LoginReady and no session grant.
        var actor = actorRegistry.GetOrAdd(callerTenantId, streamerId);
        await actor.RecoverAsync(identityStore, cancellationToken: cancellationToken);
        var grant = await identityStore.LoginAsync(callerTenantId, streamerId, command.GatewayInstanceId,
            command.ConnectionId, command.NextResumeSecret, cancellationToken);
        await actor.OnSessionGrantedAsync(grant, cancellationToken);
        // This instance now speaks for the entity: put it on the control-plane record so routing
        // names the holder and the registrar keeps the lease while the process is live.
        if (ownershipRegistrar is not null)
            await ownershipRegistrar.ClaimAsync(callerTenantId, gameId, streamerId, cancellationToken);
        return Ready(grant);
    }

    public async Task<StreamerLoginReady> ResumeAsync(string callerTenantId, ResumeCommand command,
        CancellationToken cancellationToken = default)
    {
        var grant = await identityStore.ResumeAsync(callerTenantId, command.PreviousSessionId,
            command.GatewayInstanceId, command.ConnectionId, command.ResumeOperationId,
            command.PreviousSecret, command.NextSecret, cancellationToken);
        var actor = actorRegistry.GetOrAdd(callerTenantId, grant.StreamerId);
        var facts = await actor.RecoverAsync(identityStore, grant.SessionId, cancellationToken);
        await actor.OnSessionGrantedAsync(grant, cancellationToken);
        // A resume through this instance is a takeover by session rotation: the facts derived
        // above name the mounted game, so the claim lands under that scope. An entity with no
        // binding yet claims at login or at the mount instead — a scope-less claim cannot exist.
        if (ownershipRegistrar is not null && facts.ActiveBinding is { } binding)
            await ownershipRegistrar.ClaimAsync(callerTenantId, binding.GameId, grant.StreamerId,
                cancellationToken);
        return Ready(grant);
    }

    /// <summary>Gateway switch on a live session: no rotation, only the connection generation moves.</summary>
    public async Task<StreamerConnection> AttachConnectionAsync(string callerTenantId, AttachConnectionCommand command,
        CancellationToken cancellationToken = default)
    {
        var connection = await identityStore.AttachConnectionAsync(callerTenantId, command.SessionId,
            command.GatewayInstanceId, command.ConnectionId, command.OperationId, command.CurrentSecret,
            cancellationToken);
        // The actor may be absent after a process restart; its facts are re-derived on the next recovery.
        var actor = actorRegistry.FindBySession(callerTenantId, connection.SessionId);
        if (actor is not null) await actor.OnConnectionAttachedAsync(connection, cancellationToken);
        return connection;
    }

    /// <summary>Gateway close notification; stale generations are ignored by DataServer.</summary>
    public async Task<bool> ConnectionClosedAsync(string callerTenantId, string sessionId, string gatewayInstanceId,
        string connectionId, long connectionGeneration, CancellationToken cancellationToken = default)
    {
        var applied = await identityStore.ConnectionClosedAsync(callerTenantId, sessionId, gatewayInstanceId,
            connectionId, connectionGeneration, cancellationToken);
        if (applied)
        {
            var actor = actorRegistry.FindBySession(callerTenantId, sessionId);
            if (actor is not null) await actor.OnConnectionClosedAsync(cancellationToken);
        }
        return applied;
    }

    /// <summary>
    /// Consumer-side access credential check (contracts 3.1): the signature must verify, and the
    /// session it names must still be the current, unrevoked, unexpired, unbanned session of the
    /// caller's tenant. Returns the live connection for routing decisions.
    /// </summary>
    public async Task<StreamerAccessState> ValidateAccessAsync(string callerTenantId, string accessCredential,
        CancellationToken cancellationToken = default)
    {
        var identity = ChorusAccessCredential.Validate(accessCredentialKey, accessCredential, clock)
            ?? throw new AccessCredentialRejectedException();
        var state = await identityStore.GetSessionStateAsync(callerTenantId, identity.SessionId, cancellationToken)
            ?? throw new AccessCredentialRejectedException();
        if (!StringComparer.Ordinal.Equals(state.TenantId, callerTenantId) || !state.Current || state.Revoked ||
            state.Expired || state.Banned || state.SessionVersion != identity.SessionVersion ||
            !StringComparer.Ordinal.Equals(state.StreamerId, identity.StreamerId))
            throw new AccessCredentialRejectedException();
        return new StreamerAccessState(state.StreamerId, identity.SessionId, state.SessionVersion,
            state.GatewayInstanceId, state.ConnectionId, state.ConnectionGeneration,
            Online: !state.Closed && !state.Revoked && !state.Expired);
    }

    /// <summary>
    /// Starts or switches the gameplay binding on the live slot (architecture 6.2). The access
    /// credential identifies the acting session; DataServer re-checks session currency inside the
    /// switch transaction, so a taken-over connection cannot mount or switch gameplay.
    /// </summary>
    public async Task<StreamerBinding> StartGameplayAsync(string callerTenantId, string accessCredential,
        string gameId, string operationId, CancellationToken cancellationToken = default)
    {
        if (!GameAllowed(callerTenantId, gameId)) throw new TenantApplicationDeniedException();
        var access = await ValidateAccessAsync(callerTenantId, accessCredential, cancellationToken);
        var liveSessionId = await identityStore.GetLiveSessionIdAsync(callerTenantId, access.StreamerId,
            cancellationToken) ?? throw new BindingDeniedException();
        var binding = await identityStore.StartGameplayAsync(callerTenantId, liveSessionId, access.SessionId,
            gameId, operationId, cancellationToken);
        var actor = actorRegistry.Find(callerTenantId, access.StreamerId);
        if (actor is not null) await actor.OnGameplayBindingAsync(binding, cancellationToken);
        // The mount names the game scope, so an entity that skipped the claim until now (a
        // resume before any binding) goes on the record here.
        if (ownershipRegistrar is not null)
            await ownershipRegistrar.ClaimAsync(callerTenantId, gameId, access.StreamerId, cancellationToken);
        return binding;
    }

    /// <summary>The binding to restore at login or reconnect; null when nothing is mounted.</summary>
    public async Task<StreamerBindingStatus?> GetGameplayStatusAsync(string callerTenantId, string accessCredential,
        CancellationToken cancellationToken = default)
    {
        var access = await ValidateAccessAsync(callerTenantId, accessCredential, cancellationToken);
        return await identityStore.GetActiveBindingAsync(callerTenantId, access.StreamerId, cancellationToken);
    }

    private bool GameAllowed(string tenantId, string gameId)
    {
        return tenantApplications.TryGetValue(tenantId, out var applications) &&
            applications.Values.Contains(gameId, StringComparer.Ordinal);
    }

    private async Task EnsureEntityReadyAsync(string tenantId, string gameId, string streamerId,
        CancellationToken cancellationToken)
    {
        try { await profileStore.EnsureLoadedAsync(tenantId, gameId, streamerId, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new EntityNotReadyException();
        }
    }

    private string GameIdFor(string tenantId, string appId)
    {
        if (tenantApplications.TryGetValue(tenantId, out var applications) &&
            applications.TryGetValue(appId, out var gameId) &&
            !string.IsNullOrWhiteSpace(gameId))
            return gameId;
        throw new TenantApplicationDeniedException();
    }

    private StreamerLoginReady Ready(StreamerSessionGrant grant)
    {
        var expiresAt = grant.ExpiresAtUtc;
        var credential = ChorusAccessCredential.Issue(accessCredentialKey, grant.StreamerId,
            grant.SessionId, grant.SessionVersion, expiresAt, clock);
        return new StreamerLoginReady(grant.StreamerId, grant.SessionId, grant.SessionVersion,
            credential, expiresAt, grant.Duplicate);
    }
}

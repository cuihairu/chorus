using Chorus.Platform;
using Chorus.Rpc;

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
    string StreamerId, string SessionId, long SessionVersion, DateTime ExpiresAtUtc, bool Duplicate,
    string? PreviousSessionId = null, string? PreviousGatewayInstanceId = null,
    string? PreviousConnectionId = null, long PreviousConnectionGeneration = 0);

/// <summary>Logout outcome as seen from this side of the DataServer boundary.</summary>
public sealed record StreamerSessionRevocation(
    string SessionId, string StreamerId, long SessionVersion, bool Applied,
    string? DisplacedGatewayInstanceId, string? DisplacedConnectionId, long DisplacedConnectionGeneration);

public sealed record StreamerLogout(string SessionId, string StreamerId, long SessionVersion, bool Duplicate);

/// <summary>A connection displaced by a grant or a logout; the old gateway gets the notice.</summary>
public sealed record SessionReplacement(
    string GatewayInstanceId, string SessionId, string ConnectionId, long ConnectionGeneration,
    string StreamerId, string Reason);

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

/// <summary>The mount's standing with its round participation (FIX-END-13): the room the
/// binding points at — null while the mount holds no round — and the mount's monotone round
/// generation. The next-round eligibility is computed from these facts (architecture 5.3),
/// never cached ahead of the store: the release transaction is the only writer that clears
/// the room, so the eligibility opens exactly when its receipt commits.</summary>
public sealed record StreamerBindingStatus(
    string LiveSessionId, string BindingId, string GameId, long Generation, string State,
    string? CurrentRoomId, long RoundGeneration);

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
    Task<StreamerSessionRevocation> RevokeSessionAsync(string tenantId, string sessionId,
        CancellationToken cancellationToken = default);
    Task<string?> GetSessionTenantAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<string> OpenLiveSessionAsync(string tenantId, string streamerId, string platform, string appId,
        string platformRoomKey, string canonicalLiveSessionKey, CancellationToken cancellationToken = default);
    Task<string?> GetLiveSessionIdAsync(string tenantId, string streamerId,
        CancellationToken cancellationToken = default);
    Task<StreamerBinding> StartGameplayAsync(string tenantId, string liveSessionId, string sessionId,
        string gameId, string operationId, CancellationToken cancellationToken = default);
    Task<StreamerBindingStatus?> GetActiveBindingAsync(string tenantId, string streamerId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Entity profile access owned by DataServer; login succeeds only after the load. FIX-LINK-06 adds
/// the m0 versioned read (<see cref="LoadAsync"/>) and write (<see cref="PatchAsync"/>) surface —
/// the m0 contract names the StreamerServer as the Get/PatchProfile client.
/// </summary>
public interface IStreamerProfileStore
{
    Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId, CancellationToken cancellationToken = default);

    /// <summary>Reads the versioned profile (m0 GetProfile); null when no row exists.</summary>
    Task<StreamerProfile?> LoadAsync(string tenantId, string gameId, string streamerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the versioned profile (m0 PatchProfile, versioned CAS + OperationKey idempotency).
    /// A stale <paramref name="expectedVersion"/> throws <see cref="StreamerProfileConflictException"/>.
    /// </summary>
    Task<StreamerProfile> PatchAsync(string tenantId, string gameId, string streamerId,
        string operationId, long expectedVersion, int schemaVersion, string dataJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Entity asset access owned by DataServer. FIX-LINK-07 adds the m0 versioned/idempotent write
/// surface (<see cref="ChangeAsync"/>) — the m0 contract names the StreamerServer as the
/// ChangeAsset client. Write-only: the trigger's balance read for rule validation is FIX-GM-07.
/// </summary>
public interface IStreamerAssetStore
{
    /// <summary>
    /// Changes the entity asset balance (m0 ChangeAsset, CAS "balance + delta &gt;= 0" + OperationKey
    /// idempotency). A change that would drive the balance negative throws
    /// <see cref="StreamerAssetConflictException"/>; a replay of an already-committed operation id
    /// returns the stored result with <see cref="AssetChangeResult.Duplicate"/> true.
    /// </summary>
    Task<AssetChangeResult> ChangeAsync(string tenantId, string gameId, string streamerId,
        string operationId, string assetId, long delta, string reason,
        CancellationToken cancellationToken = default);
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
    IStreamerAssetStore assetStore,
    StreamerActorRegistry actorRegistry,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tenantApplications,
    byte[] accessCredentialKey,
    Func<DateTimeOffset>? clock = null,
    StreamerOwnershipRegistrar? ownershipRegistrar = null,
    SessionReplacedNotifier? sessionNotifier = null,
    StreamerRouteSourceRegistrar? routeSourceRegistrar = null,
    StreamerResumeReadyReporter? resumeReadyReporter = null,
    StreamerMatchIntentSubmitter? matchIntentSubmitter = null)
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
        // names the holder and the registrar keeps the lease while the process is live. The
        // claim names the grant's session, so a claim carrying a rotated-out session is refused.
        if (ownershipRegistrar is not null)
            await ownershipRegistrar.ClaimAsync(callerTenantId, gameId, streamerId,
                grant.SessionId, grant.SessionVersion, cancellationToken);
        await NotifyReplacementAsync(grant, command.GatewayInstanceId, command.ConnectionId,
            "replaced_by_login", cancellationToken);
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
                grant.SessionId, grant.SessionVersion, cancellationToken);
        // A rotation displaced the previous connection wherever it lives — this gateway or
        // another. Duplicate retries carry no previous facts: the first attempt notified.
        await NotifyReplacementAsync(grant, command.GatewayInstanceId, command.ConnectionId,
            "replaced_by_resume", cancellationToken);
        return Ready(grant);
    }

    /// <summary>
    /// Logout (contracts 3.1): the access credential names the acting session, DataServer
    /// revokes it idempotently, and a displaced connection that is not the caller's own gets
    /// the replacement notice. The actor marks its connection closed; facts stay recoverable.
    /// </summary>
    public async Task<StreamerLogout> LogoutAsync(string callerTenantId, string accessCredential,
        string gatewayInstanceId, string connectionId, CancellationToken cancellationToken = default)
    {
        if (StringComparer.Ordinal.Equals(callerTenantId, "@cluster"))
        {
            // A connection that resumed in carries no application id, so the gateway cannot
            // name the tenant: the credential names the session, the session names the tenant.
            var named = ChorusAccessCredential.Validate(accessCredentialKey, accessCredential, clock)
                ?? throw new AccessCredentialRejectedException();
            callerTenantId = await identityStore.GetSessionTenantAsync(named.SessionId, cancellationToken)
                ?? throw new AccessCredentialRejectedException();
        }
        var access = await ValidateAccessAsync(callerTenantId, accessCredential, cancellationToken);
        var revocation = await identityStore.RevokeSessionAsync(callerTenantId, access.SessionId, cancellationToken);
        if (revocation.Applied)
        {
            var actor = actorRegistry.FindBySession(callerTenantId, access.SessionId);
            if (actor is not null) await actor.OnConnectionClosedAsync(cancellationToken);
        }
        if (revocation.DisplacedConnectionId is { Length: > 0 } &&
            !(StringComparer.Ordinal.Equals(revocation.DisplacedGatewayInstanceId, gatewayInstanceId) &&
              StringComparer.Ordinal.Equals(revocation.DisplacedConnectionId, connectionId)) &&
            sessionNotifier is not null)
        {
            await sessionNotifier.NotifyAsync(new SessionReplacement(
                revocation.DisplacedGatewayInstanceId!, revocation.SessionId,
                revocation.DisplacedConnectionId, revocation.DisplacedConnectionGeneration,
                revocation.StreamerId, "logged_out"), cancellationToken);
        }
        return new StreamerLogout(revocation.SessionId, revocation.StreamerId,
            revocation.SessionVersion, !revocation.Applied);
    }

    /// <summary>The owning tenant of a session, so a cluster-scoped caller can route a resume
    /// whose tenant only the session itself knows; null when the session is unknown.</summary>
    public async Task<string?> ResolveSessionTenantAsync(string sessionId,
        CancellationToken cancellationToken = default)
        => await identityStore.GetSessionTenantAsync(sessionId, cancellationToken);

    /// <summary>Gateway switch on a live session: no rotation, only the connection generation moves.</summary>
    public async Task<StreamerConnection> AttachConnectionAsync(string callerTenantId, AttachConnectionCommand command,
        CancellationToken cancellationToken = default)
    {
        var connection = await identityStore.AttachConnectionAsync(callerTenantId, command.SessionId,
            command.GatewayInstanceId, command.ConnectionId, command.OperationId, command.CurrentSecret,
            cancellationToken);
        // An absent actor is a process restart with no login/resume since: the connection names
        // the entity, so materialize and re-derive its facts now (FIX-LINK-05). The re-derivation
        // reads the committed binding, so the participation pointer survives a lost round notice.
        var actor = actorRegistry.FindBySession(callerTenantId, connection.SessionId);
        if (actor is null)
        {
            actor = actorRegistry.GetOrAdd(callerTenantId, connection.StreamerId);
            await actor.RecoverAsync(identityStore, connection.SessionId, cancellationToken);
        }
        await actor.OnConnectionAttachedAsync(connection, cancellationToken);
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
        StreamerBinding binding;
        try
        {
            binding = await identityStore.StartGameplayAsync(callerTenantId, liveSessionId, access.SessionId,
                gameId, operationId, cancellationToken);
        }
        catch (MountInLiveRoundException)
        {
            // FIX-SYNC-08: the refusal is the readiness signal. The session stands at the
            // current generation on its paused round (the mount is refused while the round is
            // paused), so this is the resume-ready declaration — best-effort, never blocking
            // the refusal it accompanies. The store resolves the paused round and no-ops when
            // the round is not paused.
            if (resumeReadyReporter is not null)
                await resumeReadyReporter.ReportResumeReadyAsync(callerTenantId, gameId, access.StreamerId,
                    access.SessionId, $"ready/{access.StreamerId}/{access.SessionId}", cancellationToken);
            throw;
        }
        var actor = actorRegistry.Find(callerTenantId, access.StreamerId);
        if (actor is null)
        {
            // An absent actor is a process restart whose gateway reconnected without a resume:
            // materialize the entity so the mount has an in-memory holder (FIX-LINK-05) — the
            // route-source registration and the participation notice both land on it. A freshly
            // mounted binding has no committed round, so no participation pointer is carried over.
            actor = actorRegistry.GetOrAdd(callerTenantId, access.StreamerId);
            await actor.RecoverAsync(identityStore, access.SessionId, cancellationToken);
        }
        await actor.OnGameplayBindingAsync(binding, cancellationToken);
        // The mount names the game scope, so an entity that skipped the claim until now (a
        // resume before any binding) goes on the record here.
        if (ownershipRegistrar is not null)
            await ownershipRegistrar.ClaimAsync(callerTenantId, gameId, access.StreamerId,
                access.SessionId, access.SessionVersion, cancellationToken);
        // The committed mount is the routing source (FIX-END-02): report it by binding id and
        // generation right away — the control plane derives the platform identity, and a lost
        // report is re-sent by the next mount, never by failing this one.
        if (routeSourceRegistrar is not null)
            await routeSourceRegistrar.RegisterAsync(callerTenantId, gameId, access.StreamerId,
                binding.BindingId, binding.Generation, cancellationToken);
        // The mount's match intent (FIX-LINK-03): the business path submits through the control
        // plane, which gates on the registered route source above. Best-effort like the
        // registration — a refused or lost intent never fails the mount. The partition is the
        // business default (no client-declared rules), composed in the same shape as the
        // gateway's EdgeQueueKeys.PartitionFor: intents only meet inside one partition, so
        // business-path mounts meet each other, never a rules-declaring client's window.
        if (matchIntentSubmitter is not null)
            await matchIntentSubmitter.SubmitAsync(callerTenantId, gameId, access.StreamerId,
                binding.BindingId, binding.Generation, access.SessionId,
                $"{callerTenantId}/{gameId}/-/default", operationId, cancellationToken);
        return binding;
    }

    /// <summary>The binding to restore at login or reconnect; null when nothing is mounted.</summary>
    public async Task<StreamerBindingStatus?> GetGameplayStatusAsync(string callerTenantId, string accessCredential,
        CancellationToken cancellationToken = default)
    {
        var access = await ValidateAccessAsync(callerTenantId, accessCredential, cancellationToken);
        return await identityStore.GetActiveBindingAsync(callerTenantId, access.StreamerId, cancellationToken);
    }

    /// <summary>
    /// FIX-LINK-06 (AT18): the streamer-profile write seam of the 主播业务处理者 — a versioned CAS
    /// write through the m0 surface. The trigger (GM 主播资料修正) is FIX-GM-07; the caller
    /// supplies the full new data and the operation key for idempotency. A stale
    /// <paramref name="expectedVersion"/> throws <see cref="StreamerProfileConflictException"/>.
    /// </summary>
    public async Task<StreamerProfile> PatchProfileAsync(string tenantId, string gameId, string streamerId,
        string operationId, long expectedVersion, int schemaVersion, string dataJson,
        CancellationToken cancellationToken = default)
        => await profileStore.PatchAsync(tenantId, gameId, streamerId, operationId,
            expectedVersion, schemaVersion, dataJson, cancellationToken);

    /// <summary>
    /// FIX-LINK-07 (AT18): the streamer asset-write seam of the 主播业务处理者 — an idempotent,
    /// non-negative-balance-constrained change through the m0 surface. The trigger (GM 允许的资产调整)
    /// is FIX-GM-07; the caller supplies the asset id, the signed delta, and the operation key for
    /// idempotency. A change that would drive the balance negative throws
    /// <see cref="StreamerAssetConflictException"/>; an idempotent replay returns
    /// <see cref="AssetChangeResult.Duplicate"/> true with the stored balance.
    /// </summary>
    public async Task<AssetChangeResult> ChangeAssetAsync(string tenantId, string gameId, string streamerId,
        string operationId, string assetId, long delta, string reason,
        CancellationToken cancellationToken = default)
        => await assetStore.ChangeAsync(tenantId, gameId, streamerId, operationId,
            assetId, delta, reason, cancellationToken);

    /// <summary>Best-effort active leg of session replacement (contracts 3.3): the old
    /// gateway is named by the grant, unless the displaced connection is the requester's
    /// own — a client that resumed in place already holds the winning session. If the old
    /// gateway never registered in routing, the passive leg (its next RPC fails with the
    /// replaced status) still bounds the old connection.</summary>
    private async Task NotifyReplacementAsync(StreamerSessionGrant grant, string gatewayInstanceId,
        string connectionId, string reason, CancellationToken cancellationToken)
    {
        if (sessionNotifier is null || string.IsNullOrEmpty(grant.PreviousConnectionId)) return;
        if (StringComparer.Ordinal.Equals(grant.PreviousGatewayInstanceId, gatewayInstanceId) &&
            StringComparer.Ordinal.Equals(grant.PreviousConnectionId, connectionId))
            return;
        await sessionNotifier.NotifyAsync(new SessionReplacement(
            grant.PreviousGatewayInstanceId!, grant.PreviousSessionId!, grant.PreviousConnectionId,
            grant.PreviousConnectionGeneration, grant.StreamerId, reason), cancellationToken);
    }

    private bool GameAllowed(string tenantId, string gameId)
    {
        return tenantApplications.TryGetValue(tenantId, out var applications) &&
            applications.Values.Contains(gameId, StringComparer.Ordinal);
    }

    private async Task EnsureEntityReadyAsync(string tenantId, string gameId, string streamerId,
        CancellationToken cancellationToken)
    {
        try
        {
            await profileStore.EnsureLoadedAsync(tenantId, gameId, streamerId, cancellationToken);
            // FIX-LINK-06 (AT01): the entity loads its 资料 at login through the m0 versioned read.
            // The row is ensured above, so a not-found is not a failure; a read error is.
            await profileStore.LoadAsync(tenantId, gameId, streamerId, cancellationToken);
        }
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

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
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tenantApplications,
    byte[] accessCredentialKey,
    Func<DateTimeOffset>? clock = null)
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
        var grant = await identityStore.LoginAsync(callerTenantId, streamerId, command.GatewayInstanceId,
            command.ConnectionId, command.NextResumeSecret, cancellationToken);
        return Ready(grant);
    }

    public async Task<StreamerLoginReady> ResumeAsync(string callerTenantId, ResumeCommand command,
        CancellationToken cancellationToken = default)
    {
        var grant = await identityStore.ResumeAsync(callerTenantId, command.PreviousSessionId,
            command.GatewayInstanceId, command.ConnectionId, command.ResumeOperationId,
            command.PreviousSecret, command.NextSecret, cancellationToken);
        return Ready(grant);
    }

    /// <summary>Gateway switch on a live session: no rotation, only the connection generation moves.</summary>
    public Task<StreamerConnection> AttachConnectionAsync(string callerTenantId, AttachConnectionCommand command,
        CancellationToken cancellationToken = default)
        => identityStore.AttachConnectionAsync(callerTenantId, command.SessionId, command.GatewayInstanceId,
            command.ConnectionId, command.OperationId, command.CurrentSecret, cancellationToken);

    /// <summary>Gateway close notification; stale generations are ignored by DataServer.</summary>
    public Task<bool> ConnectionClosedAsync(string callerTenantId, string sessionId, string gatewayInstanceId,
        string connectionId, long connectionGeneration, CancellationToken cancellationToken = default)
        => identityStore.ConnectionClosedAsync(callerTenantId, sessionId, gatewayInstanceId, connectionId,
            connectionGeneration, cancellationToken);

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

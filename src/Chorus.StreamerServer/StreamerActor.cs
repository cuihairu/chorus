using System.Collections.Concurrent;

namespace Chorus.StreamerServer;

/// <summary>StreamerActor in-memory lifecycle (architecture 5.3).</summary>
public enum StreamerActorState
{
    Loading,
    Ready,
    RetainingOffline,
    Deactivating,
    RecoveryFailed
}

/// <summary>
/// Independently observed facts (architecture 5.3): connection, login-session, live and binding
/// state are tracked separately; eligibility is computed from them, never from one online boolean.
/// </summary>
public sealed record StreamerActorFacts(
    bool SessionOnline, bool SessionCurrent, long SessionVersion,
    bool LiveOpen, StreamerBindingStatus? ActiveBinding);

/// <summary>Entity state could not be recovered; LoginReady must not be returned (chapter 5.1 step 5).</summary>
public sealed class EntityRecoveryFailedException : Exception;

/// <summary>
/// The in-memory streamer entity. The database is authoritative: every fact is (re)derived by
/// <see cref="RecoverAsync"/>, so a process restart with an empty registry converges to the same
/// facts — including a binding switch that committed just before the crash. Transitions are
/// serialized through one gate (single-mailbox discipline).
/// </summary>
public sealed class StreamerActor(string tenantId, string streamerId)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string TenantId { get; } = tenantId;
    public string StreamerId { get; } = streamerId;
    public StreamerActorState State { get; private set; } = StreamerActorState.Loading;
    public StreamerActorFacts? Facts { get; private set; }
    public string? SessionId { get; private set; }

    /// <summary>
    /// (Re)derives the entity facts from the store: live session, active binding and — when a
    /// session id is known — the session snapshot. With a session the actor lands in Ready (online)
    /// or RetainingOffline (closed/revoked/expired: retained in memory, no online control session);
    /// without one it keeps its current state until a session is granted. Any load failure enters
    /// RecoveryFailed and rethrows: login must not return ready then.
    /// </summary>
    public async Task<StreamerActorFacts> RecoverAsync(IStreamerIdentityStore store, string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StreamerActorFacts facts;
            try
            {
                var liveSessionId = await store.GetLiveSessionIdAsync(TenantId, StreamerId, cancellationToken);
                var binding = await store.GetActiveBindingAsync(TenantId, StreamerId, cancellationToken);
                StreamerSessionState? session = sessionId is null
                    ? null
                    : await store.GetSessionStateAsync(TenantId, sessionId, cancellationToken);
                var sessionCurrent = session is not null && session.Current && !session.Revoked && !session.Expired;
                var online = sessionCurrent && session is not null && !session.Closed;
                facts = new StreamerActorFacts(online, sessionCurrent, session?.SessionVersion ?? 0L,
                    liveSessionId is not null, binding);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                State = StreamerActorState.RecoveryFailed;
                throw new EntityRecoveryFailedException();
            }
            SessionId = sessionId ?? SessionId;
            Facts = facts;
            // An explicit Deactivating decision survives re-derivation; only its facts are refreshed.
            if (sessionId is not null && State != StreamerActorState.Deactivating)
                State = facts.SessionOnline ? StreamerActorState.Ready : StreamerActorState.RetainingOffline;
            return facts;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>CommitLogin/Resume granted a session and bound the connection: the entity is Ready.</summary>
    public async Task OnSessionGrantedAsync(StreamerSessionGrant grant,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SessionId = grant.SessionId;
            Facts = SessionFacts(online: true, current: true, grant.SessionVersion);
            State = StreamerActorState.Ready;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>AttachConnection bound a new gateway connection without rotating secrets.</summary>
    public async Task OnConnectionAttachedAsync(StreamerConnection connection,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SessionId = connection.SessionId;
            Facts = SessionFacts(online: true, current: true, connection.SessionVersion);
            State = StreamerActorState.Ready;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>DataServer applied the gateway close: retain the entity offline for resume.</summary>
    public async Task OnConnectionClosedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Facts is not null) Facts = Facts with { SessionOnline = false };
            if (State == StreamerActorState.Ready) State = StreamerActorState.RetainingOffline;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Keeps the in-memory projection current after a start/switch committed.</summary>
    public async Task OnGameplayBindingAsync(StreamerBinding binding,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Facts = new StreamerActorFacts(Facts?.SessionOnline ?? false, Facts?.SessionCurrent ?? false,
                Facts?.SessionVersion ?? 0L, LiveOpen: true,
                new StreamerBindingStatus(binding.LiveSessionId, binding.BindingId, binding.GameId,
                    binding.Generation, binding.State));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Begins teardown; only valid from Ready or RetainingOffline.</summary>
    public async Task<bool> BeginDeactivateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State is not (StreamerActorState.Ready or StreamerActorState.RetainingOffline))
                return false;
            State = StreamerActorState.Deactivating;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Chapter 5.3: the entity may unload only with no valid binding, session, or pending work
    /// (participation and pending transactions are not modeled in M1).
    /// </summary>
    public bool CanUnload => State is StreamerActorState.Deactivating or StreamerActorState.RecoveryFailed
        && Facts?.ActiveBinding is null && Facts?.SessionCurrent is not true;

    private StreamerActorFacts SessionFacts(bool online, bool current, long sessionVersion) =>
        new(online, current, sessionVersion, Facts?.LiveOpen ?? false, Facts?.ActiveBinding);
}

/// <summary>
/// In-memory actor index for this process. Entries lost on a crash or restart are rebuilt by
/// <see cref="StreamerActor.RecoverAsync"/> on the next login/resume, so the registry is a
/// projection of database state, never the authority.
/// </summary>
public sealed class StreamerActorRegistry
{
    private readonly ConcurrentDictionary<(string Tenant, string Streamer), StreamerActor> _actors = new();

    public StreamerActor GetOrAdd(string tenantId, string streamerId) =>
        _actors.GetOrAdd((tenantId, streamerId), _ => new StreamerActor(tenantId, streamerId));

    public StreamerActor? Find(string tenantId, string streamerId) =>
        _actors.TryGetValue((tenantId, streamerId), out var actor) ? actor : null;

    /// <summary>The actor currently holding the session; absent after a restart until re-login.</summary>
    public StreamerActor? FindBySession(string tenantId, string sessionId) =>
        _actors.Values.FirstOrDefault(actor =>
            StringComparer.Ordinal.Equals(actor.TenantId, tenantId) &&
            StringComparer.Ordinal.Equals(actor.SessionId, sessionId));

    public bool Remove(StreamerActor actor) => _actors.TryRemove((actor.TenantId, actor.StreamerId), out _);
}

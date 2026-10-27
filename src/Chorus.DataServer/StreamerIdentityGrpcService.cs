using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

/// <summary>
/// Identity and session persistence boundary used by StreamerServer orchestration. The tenant
/// claim travels in the request and is verified against the auth.identity mapping; a
/// cluster-scoped streamer-server process cannot touch another tenant's streamer.
/// </summary>
public sealed class StreamerIdentityGrpcService(
    RedesignIdentityStore identityStore, RedesignPlayerStore playerStore,
    RedesignBindingStore bindingStore, TrustedProcessCertificates trustedCertificates)
    : StreamerIdentity.StreamerIdentityBase
{
    private const int DefaultSchemaVersion = 1;
    private const string DefaultProfileJson = """{"display":"new-streamer"}""";

    public override async Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        return new ReadyReply { Ready = true, SchemaVersion = "4" };
    }

    public override async Task<ResolvedIdentityReply> ResolveIdentity(ResolveIdentityRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        var resolved = await identityStore.ResolveIdentityAsync(request.TenantId, request.Platform,
            request.AppId, request.OpenId, context.CancellationToken);
        return new ResolvedIdentityReply { StreamerId = resolved.StreamerId };
    }

    public override async Task<SessionGrantReply> Login(SessionLoginRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        await RequireIdentityTenantAsync(request.TenantId, request.StreamerId, context.CancellationToken);
        try
        {
            var grant = await identityStore.LoginAsync(request.StreamerId, request.GatewayInstanceId, request.ConnectionId,
                request.NextResumeSecret.ToByteArray(), context.CancellationToken);
            return Grant(grant);
        }
        catch (SessionRevokedException) { throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked.")); }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<SessionGrantReply> Resume(SessionResumeRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        // Fence cross-tenant use before the store rotates the secret.
        var streamerId = await identityStore.GetSessionStreamerAsync(request.PreviousSessionId, context.CancellationToken);
        if (streamerId is not null)
            await RequireIdentityTenantAsync(request.TenantId, streamerId, context.CancellationToken);
        try
        {
            var grant = await identityStore.ResumeAsync(request.PreviousSessionId, request.GatewayInstanceId,
                request.ConnectionId, request.ResumeOperationId, request.PreviousSecret.ToByteArray(),
                request.NextSecret.ToByteArray(), context.CancellationToken);
            return Grant(grant);
        }
        catch (SessionRevokedException) { throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked.")); }
        catch (SessionReplacedException) { throw new RpcException(new Status(StatusCode.FailedPrecondition, "Session is replaced.")); }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<EnsureProfileReply> EnsureEntityProfile(EnsureProfileRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        await RequireIdentityTenantAsync(request.TenantId, request.StreamerId, context.CancellationToken);
        var token = context.CancellationToken;
        var existing = await playerStore.GetProfileAsync(request.TenantId, request.GameId, request.StreamerId, token);
        if (existing is not null)
            return new EnsureProfileReply { Found = true, Version = existing.Version };
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            ExpectedVersion = 0L, SchemaVersion = DefaultSchemaVersion, DataJson = DefaultProfileJson
        }));
        try
        {
            var (value, _) = await playerStore.PatchProfileAsync(
                new OperationIdentity(request.TenantId, request.GameId, "profile.patch",
                    request.StreamerId, "login.profile-init." + request.StreamerId),
                fingerprint, 0, DefaultSchemaVersion, DefaultProfileJson, token);
            return new EnsureProfileReply { Found = true, Version = value.Version };
        }
        catch (RedesignVersionConflictException)
        {
            // A concurrent login created the profile first.
            var confirmed = await playerStore.GetProfileAsync(request.TenantId, request.GameId, request.StreamerId, token);
            return confirmed is null
                ? throw new RpcException(new Status(StatusCode.FailedPrecondition, "The entity profile could not be initialized."))
                : new EnsureProfileReply { Found = true, Version = confirmed.Version };
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The entity profile could not be initialized."));
        }
    }

    public override async Task<SessionConnectionReply> AttachConnection(SessionAttachRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        var streamerId = await identityStore.GetSessionStreamerAsync(request.SessionId, context.CancellationToken);
        if (streamerId is not null)
            await RequireIdentityTenantAsync(request.TenantId, streamerId, context.CancellationToken);
        try
        {
            var connection = await identityStore.AttachConnectionAsync(request.SessionId, request.GatewayInstanceId,
                request.ConnectionId, request.OperationId, request.CurrentSecret.ToByteArray(),
                context.CancellationToken);
            return new SessionConnectionReply
            {
                SessionId = connection.SessionId, StreamerId = connection.StreamerId,
                SessionVersion = connection.SessionVersion,
                ConnectionGeneration = connection.ConnectionGeneration, Duplicate = connection.Duplicate
            };
        }
        catch (SessionRevokedException) { throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked.")); }
        catch (SessionReplacedException) { throw new RpcException(new Status(StatusCode.FailedPrecondition, "Session is replaced.")); }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<SessionCloseReply> ConnectionClosed(SessionCloseRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        var streamerId = await identityStore.GetSessionStreamerAsync(request.SessionId, context.CancellationToken);
        if (streamerId is null) return new SessionCloseReply { Applied = false };
        await RequireIdentityTenantAsync(request.TenantId, streamerId, context.CancellationToken);
        var applied = await identityStore.CloseConnectionAsync(request.SessionId, request.GatewayInstanceId,
            request.ConnectionId, request.ConnectionGeneration, context.CancellationToken);
        return new SessionCloseReply { Applied = applied };
    }

    public override async Task<SessionStateReply> GetSessionState(SessionStateRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        var snapshot = await identityStore.GetSessionStateAsync(request.SessionId, context.CancellationToken);
        if (snapshot is null || !StringComparer.Ordinal.Equals(snapshot.TenantId, request.TenantId))
            throw new RpcException(new Status(StatusCode.NotFound, "The session does not exist for this tenant."));
        return new SessionStateReply
        {
            StreamerId = snapshot.StreamerId, TenantId = snapshot.TenantId,
            SessionVersion = snapshot.SessionVersion,
            ExpiresAtUnixMs = new DateTimeOffset(snapshot.ExpiresAtUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            GatewayInstanceId = snapshot.GatewayInstanceId, ConnectionId = snapshot.ConnectionId,
            ConnectionGeneration = snapshot.ConnectionGeneration,
            Revoked = snapshot.Revoked, Closed = snapshot.Closed, Expired = snapshot.Expired,
            Banned = snapshot.Banned, Current = snapshot.Current
        };
    }

    public override async Task<OpenLiveReply> OpenLiveSession(OpenLiveRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        await RequireIdentityTenantAsync(request.TenantId, request.StreamerId, context.CancellationToken);
        try
        {
            var liveSessionId = await bindingStore.OpenLiveSessionAsync(request.TenantId, request.StreamerId,
                request.Platform, request.AppId, request.PlatformRoomKey, request.CanonicalLiveSessionKey,
                context.CancellationToken);
            return new OpenLiveReply { LiveSessionId = liveSessionId };
        }
        catch (BindingDeniedException)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The live session is not available."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<BindingStartReply> StartGameplayBinding(BindingStartRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        var streamerId = await identityStore.GetSessionStreamerAsync(request.SessionId, context.CancellationToken);
        if (streamerId is not null)
            await RequireIdentityTenantAsync(request.TenantId, streamerId, context.CancellationToken);
        try
        {
            var grant = await bindingStore.StartGameplayAsync(request.TenantId, request.LiveSessionId,
                request.SessionId, request.GameId, request.OperationId, context.CancellationToken);
            return new BindingStartReply
            {
                BindingId = grant.BindingId, LiveSessionId = grant.LiveSessionId, GameId = grant.GameId,
                Generation = grant.Generation, State = grant.State, Duplicate = grant.Duplicate
            };
        }
        catch (BindingDeniedException)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The gameplay binding is not available."));
        }
        catch (SessionRevokedException) { throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked.")); }
        catch (SessionReplacedException) { throw new RpcException(new Status(StatusCode.FailedPrecondition, "Session is replaced.")); }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<BindingStatusReply> GetBindingStatus(BindingStatusRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        await RequireIdentityTenantAsync(request.TenantId, request.StreamerId, context.CancellationToken);
        var status = await bindingStore.GetActiveBindingAsync(request.TenantId, request.StreamerId,
            context.CancellationToken);
        return status is null
            ? new BindingStatusReply()
            : new BindingStatusReply
            {
                LiveSessionId = status.LiveSessionId, BindingId = status.BindingId, GameId = status.GameId,
                Generation = status.Generation, State = status.State
            };
    }

    public override async Task<LiveSessionReply> GetLiveSession(LiveSessionRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server");
        await RequireIdentityTenantAsync(request.TenantId, request.StreamerId, context.CancellationToken);
        var liveSessionId = await bindingStore.GetLiveSessionIdAsync(request.TenantId, request.StreamerId,
            context.CancellationToken);
        return new LiveSessionReply { LiveSessionId = liveSessionId ?? "" };
    }

    private async Task<ProcessIdentity> RequireAsync(ServerCallContext context, params string[] roles)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!roles.Contains(caller.Role, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
        return caller;
    }

    private async Task RequireIdentityTenantAsync(string tenantId, string streamerId, CancellationToken token)
    {
        var ownerTenant = await identityStore.GetIdentityTenantAsync(streamerId, token);
        if (ownerTenant is null || !StringComparer.Ordinal.Equals(ownerTenant, tenantId))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Identity is not authorized for this caller."));
    }

    private static SessionGrantReply Grant(SessionGrant grant) => new()
    {
        StreamerId = grant.StreamerId, SessionId = grant.SessionId, SessionVersion = grant.SessionVersion,
        ExpiresAtUnixMs = new DateTimeOffset(grant.ExpiresAtUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        Duplicate = grant.Duplicate
    };
}

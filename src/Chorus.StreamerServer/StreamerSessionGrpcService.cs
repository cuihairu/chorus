using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.StreamerServer;

/// <summary>The StreamerSession boundary: per-tenant certificates or a cluster-scoped edge
/// gateway that names the tenant per request, safe error mapping, no secrets echoed.</summary>
public sealed class StreamerSessionGrpcService(
    RedesignStreamerLoginCoordinator coordinator, TrustedProcessCertificates trustedCertificates)
    : StreamerSession.StreamerSessionBase
{
    public override async Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        await RequireAsync(context);
        return new ReadyReply { Ready = true, SchemaVersion = "4" };
    }

    public override async Task<LoginReadyReply> Login(LoginRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context);
        try
        {
            var ready = await coordinator.LoginAsync(TenantFor(caller, request.TenantId), new LoginCommand(
                request.Platform, request.AppId, request.LaunchCredential, request.LoginAttemptId,
                request.NextResumeSecret.ToByteArray(), request.GatewayInstanceId, request.ConnectionId),
                context.CancellationToken);
            return Reply(ready);
        }
        catch (PlatformLaunchRejectedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The platform launch credential is rejected."));
        }
        catch (TenantApplicationDeniedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The application is not mapped for this tenant."));
        }
        catch (EntityNotReadyException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The streamer entity is not ready."));
        }
        catch (EntityRecoveryFailedException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The streamer entity could not be recovered."));
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The login request is invalid."));
        }
    }

    public override async Task<LoginReadyReply> Resume(ResumeRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context);
        try
        {
            // The resume carries no application id, so a cluster-scoped gateway cannot name the
            // tenant: the session itself says who owns it. The fence below still applies.
            var tenant = TenantFor(caller, request.TenantId);
            if (StringComparer.Ordinal.Equals(tenant, "@cluster"))
            {
                tenant = await coordinator.ResolveSessionTenantAsync(request.PreviousSessionId, context.CancellationToken)
                    ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
            }
            var ready = await coordinator.ResumeAsync(tenant, new ResumeCommand(
                request.PreviousSessionId, request.ResumeOperationId, request.PreviousSecret.ToByteArray(),
                request.NextSecret.ToByteArray(), request.GatewayInstanceId, request.ConnectionId),
                context.CancellationToken);
            return Reply(ready);
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
        }
        catch (SessionReplacedException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Session is replaced."));
        }
        catch (EntityRecoveryFailedException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The streamer entity could not be recovered."));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The resume request is invalid."));
        }
    }

    public override async Task<LogoutReply> Logout(LogoutRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context);
        try
        {
            var logout = await coordinator.LogoutAsync(TenantFor(caller, request.TenantId),
                request.AccessCredential, request.GatewayInstanceId, request.ConnectionId,
                context.CancellationToken);
            return new LogoutReply
            {
                SessionId = logout.SessionId, StreamerId = logout.StreamerId,
                SessionVersion = logout.SessionVersion, Duplicate = logout.Duplicate
            };
        }
        catch (AccessCredentialRejectedException)
        {
            // A retry after a lost reply lands here: the session is already revoked, which is
            // the outcome the client wanted — the gateway maps it to the same visible error.
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The access credential is rejected."));
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The logout request is invalid."));
        }
    }

    public override async Task<ConnectionReply> AttachConnection(AttachConnectionRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context);
        try
        {
            var connection = await coordinator.AttachConnectionAsync(TenantFor(caller, request.TenantId), new AttachConnectionCommand(
                request.SessionId, request.OperationId, request.CurrentSecret.ToByteArray(),
                request.GatewayInstanceId, request.ConnectionId), context.CancellationToken);
            return new ConnectionReply
            {
                SessionId = connection.SessionId, StreamerId = connection.StreamerId,
                SessionVersion = connection.SessionVersion,
                ConnectionGeneration = connection.ConnectionGeneration, Duplicate = connection.Duplicate
            };
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
        }
        catch (SessionReplacedException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Session is replaced."));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The attach request is invalid."));
        }
    }

    public override async Task<ConnectionClosedReply> ConnectionClosed(ConnectionClosedRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context);
        try
        {
            var applied = await coordinator.ConnectionClosedAsync(TenantFor(caller, request.TenantId), request.SessionId,
                request.GatewayInstanceId, request.ConnectionId, request.ConnectionGeneration,
                context.CancellationToken);
            return new ConnectionClosedReply { Applied = applied };
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The close request is invalid."));
        }
    }

    public override async Task<AccessStateReply> ValidateAccess(ValidateAccessRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context);
        try
        {
            var access = await coordinator.ValidateAccessAsync(TenantFor(caller, request.TenantId), request.AccessCredential,
                context.CancellationToken);
            return new AccessStateReply
            {
                StreamerId = access.StreamerId, SessionId = access.SessionId,
                SessionVersion = access.SessionVersion, ConnectionGeneration = access.ConnectionGeneration,
                GatewayInstanceId = access.GatewayInstanceId, ConnectionId = access.ConnectionId,
                Online = access.Online
            };
        }
        catch (AccessCredentialRejectedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The access credential is rejected."));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The access request is invalid."));
        }
    }

    public override async Task<GameplayStatusReply> StartGameplay(StartGameplayRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context);
        try
        {
            var binding = await coordinator.StartGameplayAsync(TenantFor(caller, request.TenantId), request.AccessCredential,
                request.GameId, request.OperationId, context.CancellationToken);
            return StatusReply(binding.LiveSessionId, binding.BindingId, binding.GameId,
                binding.Generation, binding.State, binding.Duplicate);
        }
        catch (TenantApplicationDeniedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The application is not mapped for this tenant."));
        }
        catch (AccessCredentialRejectedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The access credential is rejected."));
        }
        catch (BindingDeniedException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The gameplay binding is not available."));
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
        }
        catch (SessionReplacedException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Session is replaced."));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The gameplay request is invalid."));
        }
    }

    public override async Task<GameplayStatusReply> GameplayStatus(GameplayStatusRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context);
        try
        {
            var status = await coordinator.GetGameplayStatusAsync(TenantFor(caller, request.TenantId), request.AccessCredential,
                context.CancellationToken);
            return status is null
                ? new GameplayStatusReply()
                : StatusReply(status.LiveSessionId, status.BindingId, status.GameId,
                    status.Generation, status.State, false);
        }
        catch (AccessCredentialRejectedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The access credential is rejected."));
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The status request is invalid."));
        }
    }

    private static GameplayStatusReply StatusReply(
        string liveSessionId, string bindingId, string gameId, long generation, string state, bool duplicate)
        => new()
        {
            LiveSessionId = liveSessionId, BindingId = bindingId, GameId = gameId,
            Generation = generation, State = state, Duplicate = duplicate
        };

    private async Task<ProcessIdentity> RequireAsync(ServerCallContext context)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!StringComparer.Ordinal.Equals(caller.Role, "streamer-server")
            && !StringComparer.Ordinal.Equals(caller.Role, "streamer-gateway"))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
        return caller;
    }

    /// <summary>
    /// Tenant resolution for the two caller shapes: a per-tenant certificate names nothing in
    /// the request (its scope is the tenant), a cluster-scoped caller — the WSS edge gateway —
    /// names the tenant per request. DataServer still fences every request against the
    /// auth.identity mapping, so a named tenant can never cross the fence.
    /// </summary>
    private static string TenantFor(ProcessIdentity caller, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return caller.TenantId;
        if (!StringComparer.Ordinal.Equals(caller.TenantId, "@cluster")
            && !StringComparer.Ordinal.Equals(caller.TenantId, requested))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        return requested;
    }

    private static LoginReadyReply Reply(StreamerLoginReady ready) => new()
    {
        StreamerId = ready.StreamerId, SessionId = ready.SessionId, SessionVersion = ready.SessionVersion,
        AccessCredential = ready.AccessCredential,
        AccessExpiresAtUnixMs = ready.AccessExpiresAtUtc.ToUnixTimeMilliseconds(),
        Duplicate = ready.Duplicate,
        // The reply exists only after the grant transaction committed (contracts 3.3): the
        // rotation receipt is durable by construction.
        ResumeRegistered = true
    };
}

using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.StreamerServer;

/// <summary>The StreamerSession boundary: certificate-scoped tenant, safe error mapping, no secrets echoed.</summary>
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
            var ready = await coordinator.LoginAsync(caller.TenantId, new LoginCommand(
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
            var ready = await coordinator.ResumeAsync(caller.TenantId, new ResumeCommand(
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
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The resume request is invalid."));
        }
    }

    public override async Task<ConnectionReply> AttachConnection(AttachConnectionRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context);
        try
        {
            var connection = await coordinator.AttachConnectionAsync(caller.TenantId, new AttachConnectionCommand(
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
            var applied = await coordinator.ConnectionClosedAsync(caller.TenantId, request.SessionId,
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
            var access = await coordinator.ValidateAccessAsync(caller.TenantId, request.AccessCredential,
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

    private async Task<ProcessIdentity> RequireAsync(ServerCallContext context)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!StringComparer.Ordinal.Equals(caller.Role, "streamer-server"))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
        return caller;
    }

    private static LoginReadyReply Reply(StreamerLoginReady ready) => new()
    {
        StreamerId = ready.StreamerId, SessionId = ready.SessionId, SessionVersion = ready.SessionVersion,
        AccessCredential = ready.AccessCredential,
        AccessExpiresAtUnixMs = ready.AccessExpiresAtUtc.ToUnixTimeMilliseconds(),
        Duplicate = ready.Duplicate
    };
}

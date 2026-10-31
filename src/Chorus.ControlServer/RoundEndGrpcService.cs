using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// The end operation boundary (architecture 6.4, FIX-END-01). The game host's step-1
/// notification persists one operation per round: the business id, the room's round
/// generation, the mount generations that entered it, the phase ladder and the last error.
/// Creation is idempotent by request identity — the same begin replays the same operation,
/// a second business id for the same room refuses — and the generation fence refuses a late
/// begin without writing, so a request naming a superseded generation cannot touch the
/// round the mounts moved on to. A restarted control plane reads the same rows and
/// continues under the same business id.
/// </summary>
public sealed class RoundEndGrpcService(
    RedesignControlStore controlStore, TrustedProcessCertificates trustedCertificates)
    : RoundEnd.RoundEndBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "game-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<BeginEndReply> BeginEnd(BeginEndRequest request,
        ServerCallContext context)
    {
        // Step 1's caller is the owning game host; the control plane itself may drive the
        // same begin when it resumes a round whose notification never landed.
        Require(context, "control-server", "game-server");
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.EndOperationId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The begin request is incomplete."));
        if (request.RoundGeneration <= 0 || request.FinalTick < 0 || request.ResultVersion < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The begin's generations and result versions must be positive facts."));
        try
        {
            var (operation, duplicate) = await controlStore.BeginEndOperationAsync(
                request.TenantId, request.GameId, request.RoomId, request.EndOperationId,
                request.RoundGeneration, request.FinalTick, request.ResultVersion,
                context.CancellationToken);
            return new BeginEndReply
            {
                EndOperationId = operation.EndOperationId, Phase = operation.Phase,
                Duplicate = duplicate
            };
        }
        catch (StaleRoundGenerationException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (EndOperationConflictException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
    }

    public override async Task<GetEndStatusReply> GetEndStatus(GetEndStatusRequest request,
        ServerCallContext context)
    {
        Require(context, "control-server", "game-server");
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The status request is incomplete."));
        var operation = await controlStore.GetEndOperationAsync(
            request.TenantId, request.GameId, request.RoomId, context.CancellationToken);
        if (operation is null)
            return new GetEndStatusReply { Found = false };
        var reply = new GetEndStatusReply
        {
            Found = true,
            EndOperationId = operation.EndOperationId,
            Phase = operation.Phase,
            RoundGeneration = operation.RoundGeneration,
            FinalTick = operation.FinalTick,
            ResultVersion = operation.ResultVersion,
            LastError = operation.LastError ?? ""
        };
        reply.BindingGenerations.AddRange(operation.BindingGenerations.Select(binding =>
            new EndBindingGeneration
            {
                BindingId = binding.BindingId, Generation = binding.Generation,
                RoundGeneration = binding.RoundGeneration
            }));
        return reply;
    }

    private void Require(ServerCallContext context, params string[] roles)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                "Client certificate identity is invalid."));
        if (!roles.Contains(caller.Role, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "Role is not authorized."));
    }
}

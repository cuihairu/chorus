using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

public sealed class DataGrpcService(
    RedesignPlayerStore store, TrustedProcessCertificates trustedCertificates) : DataService.DataServiceBase
{
    public override async Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        await RequireAsync(context, "control-server", "node-agent", "streamer-server", "game-server");
        return new ReadyReply { Ready = true, SchemaVersion = "4" };
    }

    public override async Task<ReceiptReply> GetOperation(OperationKey request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "streamer-server", "game-server");
        var allowedKind = caller.Role switch
        {
            "streamer-server" => request.OperationKind is "profile.patch" or "asset.change",
            "game-server" => request.OperationKind == "fenced.probe",
            _ => false
        };
        if (!allowedKind)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role cannot query this operation kind."));
        var identity = Identity(request, caller);
        var fingerprint = OperationFingerprint.FromStored(request.CanonicalVersion, request.RequestHashHex);
        var result = await store.GetOperationAsync(identity, fingerprint, context.CancellationToken);
        return result is null ? new ReceiptReply { Found = false }
            : new ReceiptReply { Found = true, Duplicate = true, ResultJson = result.ResultJson };
    }

    public override async Task<ProfileReply> GetProfile(ProfileRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "streamer-server");
        CheckTenant(caller, request.TenantId);
        var value = await store.GetProfileAsync(request.TenantId, request.GameId, request.PlayerId, context.CancellationToken);
        return value is null ? new ProfileReply { Found = false }
            : new ProfileReply { Found = true, Version = value.Version, SchemaVersion = value.SchemaVersion, DataJson = value.DataJson };
    }

    public override async Task<ProfileReply> PatchProfile(PatchProfileRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "streamer-server");
        var identity = Identity(request.Key, caller, "profile.patch");
        var fingerprint = Fingerprint(request.Key, new { request.ExpectedVersion, request.SchemaVersion, request.DataJson });
        try
        {
            var (value, duplicate) = await store.PatchProfileAsync(identity, fingerprint, request.ExpectedVersion,
                request.SchemaVersion, request.DataJson, context.CancellationToken);
            return new ProfileReply { Found = true, Version = value.Version, SchemaVersion = value.SchemaVersion,
                DataJson = value.DataJson, Duplicate = duplicate };
        }
        catch (RedesignVersionConflictException) { throw new RpcException(new Status(StatusCode.Aborted, "Profile version conflict.")); }
        catch (IdempotencyConflictException) { throw new RpcException(new Status(StatusCode.AlreadyExists, "Operation ID conflict.")); }
    }

    public override async Task<AssetReply> GetAsset(AssetRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "streamer-server");
        CheckTenant(caller, request.TenantId);
        var value = await store.GetAssetAsync(request.TenantId, request.GameId, request.PlayerId,
            request.AssetId, context.CancellationToken);
        return new AssetReply { Balance = value.Balance, Version = value.Version };
    }

    public override async Task<AssetReply> ChangeAsset(ChangeAssetRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "streamer-server");
        var identity = Identity(request.Key, caller, "asset.change");
        var fingerprint = Fingerprint(request.Key, new { request.AssetId, request.Delta, request.Reason });
        try
        {
            var (value, duplicate) = await store.ChangeAssetAsync(identity, fingerprint, request.AssetId,
                request.Delta, request.Reason, context.CancellationToken);
            return new AssetReply { Balance = value.Balance, Version = value.Version, Duplicate = duplicate };
        }
        catch (RedesignVersionConflictException) { throw new RpcException(new Status(StatusCode.Aborted, "Asset balance constraint.")); }
        catch (IdempotencyConflictException) { throw new RpcException(new Status(StatusCode.AlreadyExists, "Operation ID conflict.")); }
    }

    public override async Task<FencedProbeReply> CommitFencedProbe(FencedProbeRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "game-server");
        var identity = Identity(request.Key, caller, "fenced.probe");
        if (request.OwnerInstanceId != caller.ProcessInstanceId)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Owner instance differs from certificate."));
        var fingerprint = Fingerprint(request.Key, new { request.OwnerKind, request.EntityId,
            request.OwnerInstanceId, request.Epoch, request.EffectId });
        try
        {
            var result = await store.CommitFencedProbeAsync(identity, fingerprint, request.OwnerKind,
                request.EntityId, request.OwnerInstanceId, request.Epoch, request.EffectId, context.CancellationToken);
            return new FencedProbeReply { Duplicate = result.Duplicate, EffectId = request.EffectId };
        }
        catch (RedesignStaleOwnerException) { throw new RpcException(new Status(StatusCode.FailedPrecondition, "Stale owner.")); }
        catch (IdempotencyConflictException) { throw new RpcException(new Status(StatusCode.AlreadyExists, "Operation ID conflict.")); }
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

    private static OperationIdentity Identity(OperationKey key, ProcessIdentity caller, string? kind = null)
    {
        if (key is null || (kind is not null && key.OperationKind != kind))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Operation key is invalid."));
        CheckTenant(caller, key.TenantId);
        try { return new OperationIdentity(key.TenantId, key.GameId, key.OperationKind, key.TargetId, key.OperationId); }
        catch (ArgumentException) { throw new RpcException(new Status(StatusCode.InvalidArgument, "Operation key is invalid.")); }
    }

    private static OperationFingerprint Fingerprint(OperationKey key, object canonicalRequest)
    {
        if (key.CanonicalVersion != 1)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Unsupported canonical version."));
        var computed = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(canonicalRequest));
        if (!StringComparer.Ordinal.Equals(key.RequestHashHex, computed.Sha256Hex))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request fingerprint differs."));
        return computed;
    }

    private static void CheckTenant(ProcessIdentity caller, string tenantId)
    {
        if (!StringComparer.Ordinal.Equals(caller.TenantId, tenantId))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
    }
}

using Grpc.Core;

namespace Chorus.Rpc;

/// <summary>
/// The minimal GM client call example (FIX-GM-06, architecture 12.1). A GM tool talks only to
/// the ControlServer Admin entry and never reaches a GameServer or DataServer directly; it
/// carries the operator's bearer credential in the "Authorization: Bearer" header, which the
/// ControlServer resolves to a human operator identity (role and tenant/game scope). The
/// request body's operator field is the operator's own id — the server rejects a mismatch as a
/// forged identity, so the client always fills it from the credential it was issued with.
/// </summary>
public sealed class GmOperatorClient(Admin.AdminClient admin, string operatorId, string operatorToken)
{
    private Metadata Credential() => new() { { "authorization", "Bearer " + operatorToken } };

    /// <summary>Reads a room's epoch (FIX-GM-01). Read-only: no audit row, no state change.</summary>
    public async Task<AdminGetRoomEpochReply> GetRoomEpochAsync(string tenantId, string gameId,
        string roomId, CancellationToken cancellationToken = default) =>
        await admin.GetRoomEpochAsync(new AdminGetRoomEpochRequest
        {
            TenantId = tenantId, GameId = gameId, RoomId = roomId, Operator = operatorId
        }, Credential(), cancellationToken: cancellationToken);

    /// <summary>The audited, idempotent epoch bump (FIX-GM-01/02/03). The reply separates the
    /// storage commit (old/new epoch) from the platform gateway's route confirmation
    /// (route_sync_status); the request id is the OperationId a lost reply replays against.</summary>
    public async Task<AdminBumpRoomEpochReply> BumpRoomEpochAsync(string tenantId, string gameId,
        string roomId, string requestId, string reason, long expectedEpoch, long targetEpoch = 0,
        CancellationToken cancellationToken = default) =>
        await admin.BumpRoomEpochAsync(new AdminBumpRoomEpochRequest
        {
            TenantId = tenantId, GameId = gameId, RoomId = roomId, RequestId = requestId,
            Reason = reason, Operator = operatorId, ExpectedEpoch = expectedEpoch, TargetEpoch = targetEpoch
        }, Credential(), cancellationToken: cancellationToken);

    /// <summary>The targeted playtest dispatch (FIX-GM-07, architecture 12.1). The payload is
    /// the module's own command shape played as the named participant; the reply's stage is
    /// the dispatch disposition (never "Applied" — the effect query below proves that).</summary>
    public async Task<AdminDispatchRoomCommandReply> DispatchRoomCommandAsync(string tenantId,
        string gameId, string roomId, string requestId, string reason, string participantId,
        string payloadJson, long expectedEpoch = 0, CancellationToken cancellationToken = default) =>
        await admin.DispatchRoomCommandAsync(new AdminDispatchRoomCommandRequest
        {
            TenantId = tenantId, GameId = gameId, RoomId = roomId, RequestId = requestId,
            Reason = reason, Operator = operatorId, ParticipantId = participantId,
            PayloadJson = payloadJson, ExpectedEpoch = expectedEpoch
        }, Credential(), cancellationToken: cancellationToken);

    /// <summary>The effect query by the original operation id (FIX-GM-07): accepted, applied
    /// (with the consuming tick) and not-found are distinct answers — a timeout is queried,
    /// never re-sent under a new id.</summary>
    public async Task<AdminGetOperationStatusReply> GetOperationStatusAsync(string operationId,
        string tenantId, string gameId, string roomId, CancellationToken cancellationToken = default) =>
        await admin.GetOperationStatusAsync(new AdminGetOperationStatusRequest
        {
            OperationId = operationId, TenantId = tenantId, GameId = gameId, RoomId = roomId
        }, Credential(), cancellationToken: cancellationToken);
}

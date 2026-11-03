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
}

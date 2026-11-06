using System.Text.Json;
using Chorus.Protocol;
using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerServer;

/// <summary>The streamer's versioned profile data, as stored in <c>player.profile</c>.</summary>
public sealed record StreamerProfile(long Version, int SchemaVersion, string DataJson);

/// <summary>
/// FIX-LINK-06: a profile write whose optimistic version did not match the stored row
/// (m0 <c>PatchProfile</c> CAS conflict). The caller decides whether to reload and retry.
/// </summary>
public sealed class StreamerProfileConflictException : Exception;

/// <summary>
/// Entity profile access through the DataServer. <see cref="EnsureLoadedAsync"/> is the m1 identity
/// init (default row, DataServer-side); FIX-LINK-06 adds the m0 versioned read/write surface — the
/// StreamerServer is the designated <c>DataService.Get/PatchProfile</c> client (m0 contract).
/// </summary>
public sealed class StreamerProfileChannel(Func<GrpcChannel> channelFactory) : IStreamerProfileStore
{
    public async Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId,
        CancellationToken cancellationToken = default)
    {
        using var channel = channelFactory();
        var client = new StreamerIdentity.StreamerIdentityClient(channel);
        var reply = await client.EnsureEntityProfileAsync(new EnsureProfileRequest
        {
            TenantId = tenantId, GameId = gameId, StreamerId = streamerId
        }, cancellationToken: cancellationToken);
        if (!reply.Found)
            throw new InvalidOperationException("The entity profile could not be loaded.");
    }

    public async Task<StreamerProfile?> LoadAsync(string tenantId, string gameId, string streamerId,
        CancellationToken cancellationToken = default)
    {
        using var channel = channelFactory();
        var client = new DataService.DataServiceClient(channel);
        var reply = await client.GetProfileAsync(new ProfileRequest
        {
            TenantId = tenantId, GameId = gameId, PlayerId = streamerId
        }, cancellationToken: cancellationToken);
        return reply.Found ? new StreamerProfile(reply.Version, reply.SchemaVersion, reply.DataJson) : null;
    }

    public async Task<StreamerProfile> PatchAsync(string tenantId, string gameId, string streamerId,
        string operationId, long expectedVersion, int schemaVersion, string dataJson,
        CancellationToken cancellationToken = default)
    {
        using var channel = channelFactory();
        var client = new DataService.DataServiceClient(channel);
        try
        {
            // The request fingerprint must be byte-identical to what DataServer computes from
            // its own request ({ ExpectedVersion, SchemaVersion, DataJson }), so the canonical
            // object here mirrors that shape exactly (same names, same order).
            var fingerprint = OperationFingerprint.FromCanonicalRequest(1,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    ExpectedVersion = expectedVersion, SchemaVersion = schemaVersion, DataJson = dataJson
                }));
            var reply = await client.PatchProfileAsync(new PatchProfileRequest
            {
                Key = new OperationKey
                {
                    TenantId = tenantId, GameId = gameId, OperationKind = "profile.patch",
                    TargetId = streamerId, OperationId = operationId, CanonicalVersion = 1,
                    RequestHashHex = fingerprint.Sha256Hex
                },
                ExpectedVersion = expectedVersion, SchemaVersion = schemaVersion, DataJson = dataJson
            }, cancellationToken: cancellationToken);
            return new StreamerProfile(reply.Version, reply.SchemaVersion, reply.DataJson);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Aborted)
        {
            // m0 surfaces the CAS conflict as Aborted; keep gRPC codes out of the business layer.
            throw new StreamerProfileConflictException();
        }
    }
}

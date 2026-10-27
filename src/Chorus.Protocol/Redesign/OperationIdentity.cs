using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Chorus.Protocol.Redesign;

/// <summary>The durable receipt key for one logical operation.</summary>
public sealed record OperationIdentity
{
    public const string ClusterGameId = "@cluster";

    public string TenantId { get; }
    public string GameId { get; }
    public string OperationKind { get; }
    public string TargetId { get; }
    public string OperationId { get; }

    public OperationIdentity(
        string tenantId,
        string gameId,
        string operationKind,
        string targetId,
        string operationId)
    {
        TenantId = Required(tenantId, nameof(tenantId));
        GameId = Required(gameId, nameof(gameId));
        OperationKind = Required(operationKind, nameof(operationKind));
        TargetId = Required(targetId, nameof(targetId));
        OperationId = Required(operationId, nameof(operationId));
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException("A nonblank, unpadded identifier is required.", name);
        return value;
    }
}

/// <summary>
/// Fingerprint of a versioned canonical business request. Transport request ID, trace,
/// deadline and caller instance must be excluded before passing the canonical bytes here.
/// </summary>
public sealed record OperationFingerprint
{
    public int CanonicalVersion { get; }
    public string Sha256Hex { get; }

    private OperationFingerprint(int canonicalVersion, string sha256Hex)
    {
        CanonicalVersion = canonicalVersion;
        Sha256Hex = sha256Hex;
    }

    public static OperationFingerprint FromCanonicalRequest(int canonicalVersion, ReadOnlySpan<byte> request)
    {
        if (canonicalVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(canonicalVersion));

        // Version is part of the digest, so a serializer revision cannot accidentally
        // match a receipt written under an earlier canonical contract.
        var bytes = new byte[sizeof(int) + request.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, canonicalVersion);
        request.CopyTo(bytes.AsSpan(sizeof(int)));
        return new OperationFingerprint(canonicalVersion, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}

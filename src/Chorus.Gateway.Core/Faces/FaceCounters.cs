using System.Text.Json.Serialization;

namespace Chorus.Gateway.Core.Faces;

/// <summary>
/// Per-face counters, exposed by hosts under <c>/faces</c> so the two listener
/// faces can be reasoned about independently (process-topology.md §2: two
/// fault domains).
/// </summary>
public sealed class FaceCounters
{
    // platform-ingress face
    private long _ingressAccepted;
    private long _ingressRateLimited;
    private long _ingressTooLarge;
    private long _ingressTimedOut;
    private long _ingressUnverified;
    private long _ingressDuplicates;
    private long _ingressUnmapped;

    // streamer-ws face
    private long _wsConnections;
    private long _wsUnauthorized;
    private long _wsProtocolMismatch;
    private long _wsMessageTooLarge;
    private long _wsAckAccepted;
    private long _wsAckDropped;

    [JsonPropertyName("ingress")]
    public IngressFaceSnapshot Ingress => new(
        Interlocked.Read(ref _ingressAccepted),
        Interlocked.Read(ref _ingressRateLimited),
        Interlocked.Read(ref _ingressTooLarge),
        Interlocked.Read(ref _ingressTimedOut),
        Interlocked.Read(ref _ingressUnverified),
        Interlocked.Read(ref _ingressDuplicates),
        Interlocked.Read(ref _ingressUnmapped));

    [JsonPropertyName("streamerWs")]
    public StreamerWsFaceSnapshot StreamerWs => new(
        Interlocked.Read(ref _wsConnections),
        Interlocked.Read(ref _wsUnauthorized),
        Interlocked.Read(ref _wsProtocolMismatch),
        Interlocked.Read(ref _wsMessageTooLarge),
        Interlocked.Read(ref _wsAckAccepted),
        Interlocked.Read(ref _wsAckDropped));

    public void IngressAccepted() => Interlocked.Increment(ref _ingressAccepted);
    public void IngressRateLimited() => Interlocked.Increment(ref _ingressRateLimited);
    public void IngressTooLarge() => Interlocked.Increment(ref _ingressTooLarge);
    public void IngressTimedOut() => Interlocked.Increment(ref _ingressTimedOut);
    public void IngressUnverified() => Interlocked.Increment(ref _ingressUnverified);
    public void IngressDuplicates() => Interlocked.Increment(ref _ingressDuplicates);
    public void IngressUnmapped() => Interlocked.Increment(ref _ingressUnmapped);

    public void WsConnection() => Interlocked.Increment(ref _wsConnections);
    public void WsUnauthorized() => Interlocked.Increment(ref _wsUnauthorized);
    public void WsProtocolMismatch() => Interlocked.Increment(ref _wsProtocolMismatch);
    public void WsMessageTooLarge() => Interlocked.Increment(ref _wsMessageTooLarge);
    public void WsAckAccepted() => Interlocked.Increment(ref _wsAckAccepted);
    public void WsAckDropped() => Interlocked.Increment(ref _wsAckDropped);
}

public sealed record IngressFaceSnapshot(
    long Accepted,
    long RateLimited,
    long TooLarge,
    long TimedOut,
    long Unverified,
    long Duplicates,
    long Unmapped);

public sealed record StreamerWsFaceSnapshot(
    long Connections,
    long Unauthorized,
    long ProtocolMismatch,
    long MessageTooLarge,
    long AckAccepted,
    long AckDropped);

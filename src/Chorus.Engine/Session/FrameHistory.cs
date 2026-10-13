namespace Chorus.Engine.Session;

/// <summary>
/// Frame history ring buffer (frame-sync P2, docs/design/frame-sync.md §4.3):
/// keeps the most recent canonical snapshots per session so a lagging or
/// reconnecting client can pull the missed window instead of jumping to the
/// latest frame.
///
/// Capped by total encoded bytes, not frame count - frame count times average
/// snapshot size is unbounded in the worst case (MaxSnapshotBytes each), while
/// a byte cap gives a hard per-session memory ceiling. Oldest frames evict
/// first; once a frame is evicted the window before it is answerable only
/// with the latest snapshot plus a forced flag.
///
/// Actor-confined: only the SessionActor run loop reads and writes the
/// buffer, so no locking is required.
/// </summary>
public sealed class FrameHistory
{
    private readonly List<WireSnapshot> _frames = new();
    private readonly int _capacityBytes;
    private long _bytes;

    public FrameHistory(int capacityBytes)
    {
        _capacityBytes = Math.Max(0, capacityBytes);
    }

    /// <summary>Oldest retained frame id; -1 when empty.</summary>
    public long OldestFrameId => _frames.Count == 0 ? -1 : _frames[0].Snapshot.TickId;

    /// <summary>Newest retained frame id; -1 when empty.</summary>
    public long NewestFrameId => _frames.Count == 0 ? -1 : _frames[^1].Snapshot.TickId;

    public long Bytes => _bytes;

    public int Count => _frames.Count;

    /// <summary>
    /// Appends a canonical (unforced) snapshot; evicts oldest frames until the
    /// buffer fits the byte cap. A snapshot larger than the whole cap evicts
    /// everything else and is stored alone - the newest frame is always
    /// retained so gap-fill can at least answer with the current state.
    /// </summary>
    public void Add(WireSnapshot wire)
    {
        if (_capacityBytes <= 0)
        {
            return;
        }

        _frames.Add(wire);
        _bytes += wire.EncodedJson.Length;

        // Evict from the front, but never the frame just added (index stops
        // one short of the tail): even a single oversize frame must stay
        // answerable as the latest-frame fallback.
        var index = 0;
        while (index < _frames.Count - 1 && _bytes > _capacityBytes)
        {
            _bytes -= _frames[index].EncodedJson.Length;
            index++;
        }

        if (index > 0)
        {
            _frames.RemoveRange(0, index);
        }
    }

    /// <summary>
    /// Collects retained frames in [fromFrameId, <see cref="NewestFrameId"/>].
    /// Returns false when the window start is missing (never stored or already
    /// evicted) or the buffer is empty - the caller answers with the latest
    /// snapshot plus forced=true (frame-sync.md §4.3: 窗口外的缺口 → 最新快照 + forced).
    /// A start before the oldest retained frame is also a miss: replaying only
    /// the retained tail would leave the client with a hidden gap.
    /// </summary>
    public bool TryGetWindow(long fromFrameId, out IReadOnlyList<WireSnapshot> frames)
    {
        if (_frames.Count == 0 || fromFrameId < _frames[0].Snapshot.TickId)
        {
            frames = Array.Empty<WireSnapshot>();
            return false;
        }

        // Frames are appended in tick order; find the start of the window.
        var index = _frames.FindIndex(f => f.Snapshot.TickId >= fromFrameId);
        if (index < 0)
        {
            frames = Array.Empty<WireSnapshot>();
            return false;
        }

        frames = _frames.GetRange(index, _frames.Count - index);
        return true;
    }
}

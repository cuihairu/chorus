using System.Text.Json;

namespace Chorus.DataServer;

/// <summary>The opaque continuation cursor of one recovery page (FIX-READ-05): where the
/// page stopped (stage and index into the plan's material lists) and the view it was cut
/// from (candidate depth, epoch, head tick) — a later page refuses to continue a read
/// whose view moved, so pages are never stitched across two heads.</summary>
public sealed record RecoveryPageToken(int Depth, long Epoch, long Head, int Stage, int Index);

/// <summary>
/// Slices a recovery plan into bounded pages (architecture 10.3, FIX-READ-05). The wire
/// contract is explicit limits, not an ever-larger unary message: at most
/// <see cref="RowLimit"/> rows or <see cref="ByteLimit"/> payload bytes per page —
/// whichever fills first, and always at least one row so the read always advances.
/// The material order is snapshots (newest-first), frames (tick order), deliveries
/// (routing order), participants; a cursor continues exactly past the last row its page
/// carried, so assembling every page of one view reproduces the whole plan with no
/// duplicates and no gaps. Paging holds no server state: every page re-reads the plan,
/// and a reader cancels by not asking for the next page.
/// </summary>
public static class RecoveryPager
{
    /// <summary>Rows per page at most. 256 rows keeps a typical page small while a
    /// multi-thousand-frame history still needs only a handful of round trips.</summary>
    public const int RowLimit = 256;

    /// <summary>Payload bytes per page at most (snapshots, hashes and delivery payloads
    /// combined). One megabyte leaves the default gRPC message ceiling far away.</summary>
    public const long ByteLimit = 1024 * 1024;

    private const int SnapshotStage = 0;
    private const int FrameStage = 1;
    private const int DeliveryStage = 2;
    private const int ParticipantStage = 3;

    /// <summary>Encodes a cursor as opaque bytes for the wire.</summary>
    public static byte[] Encode(RecoveryPageToken token) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        depth = token.Depth, epoch = token.Epoch, head = token.Head,
        stage = token.Stage, index = token.Index
    });

    /// <summary>Decodes a cursor, or null when the bytes are not one of ours.</summary>
    public static RecoveryPageToken? Decode(ReadOnlySpan<byte> token)
    {
        try
        {
            var document = JsonSerializer.Deserialize<JsonElement>(token);
            return new RecoveryPageToken(
                document.GetProperty("depth").GetInt32(),
                document.GetProperty("epoch").GetInt64(),
                document.GetProperty("head").GetInt64(),
                document.GetProperty("stage").GetInt32(),
                document.GetProperty("index").GetInt32());
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// One page of the plan from the cursor on: the head fields with the sliced material
    /// and the cursor the next page starts at — null when this page reached the end.
    /// The cursor must come from this plan's view (same depth, epoch and head); the
    /// caller enforces that against a fresh read before slicing.
    /// </summary>
    public static (RecoveryPlan Page, RecoveryPageToken? Next) TakePage(RecoveryPlan plan,
        RecoveryPageToken cursor)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(cursor);
        var snapshots = new List<RecoverySnapshot>();
        var frames = new List<RecoveryFrame>();
        var deliveries = new List<RecoveryDelivery>();
        var participants = new List<RoundParticipantState>();
        var (stage, index) = (cursor.Stage, cursor.Index);
        var rows = 0;
        var bytes = 0L;
        var exhausted = false;
        while (!exhausted)
        {
            // Advance one row from wherever the cursor sits, crossing stages in material
            // order; the byte cap fills a page between rows, the row cap may break it
            // exactly at a stage boundary — both only after at least one row went in.
            switch (stage)
            {
                case SnapshotStage:
                    if (index >= plan.Snapshots.Count) { stage = FrameStage; index = 0; continue; }
                    var snapshot = plan.Snapshots[index];
                    if (rows > 0 && (rows >= RowLimit ||
                            bytes + snapshot.Payload.LongLength + snapshot.PayloadHash.LongLength > ByteLimit))
                        goto page;
                    snapshots.Add(snapshot);
                    bytes += snapshot.Payload.LongLength + snapshot.PayloadHash.LongLength;
                    index++;
                    rows++;
                    if (rows >= RowLimit || bytes >= ByteLimit) goto page;
                    break;
                case FrameStage:
                    if (index >= plan.Frames.Count) { stage = DeliveryStage; index = 0; continue; }
                    var frame = plan.Frames[index];
                    if (rows > 0 && (rows >= RowLimit || bytes + frame.StateHash.LongLength > ByteLimit))
                        goto page;
                    frames.Add(frame);
                    bytes += frame.StateHash.LongLength;
                    index++;
                    rows++;
                    if (rows >= RowLimit || bytes >= ByteLimit) goto page;
                    break;
                case DeliveryStage:
                    if (index >= plan.Deliveries.Count) { stage = ParticipantStage; index = 0; continue; }
                    var delivery = plan.Deliveries[index];
                    if (rows > 0 && (rows >= RowLimit || bytes + delivery.PayloadJson.Length > ByteLimit))
                        goto page;
                    deliveries.Add(delivery);
                    bytes += delivery.PayloadJson.Length;
                    index++;
                    rows++;
                    if (rows >= RowLimit || bytes >= ByteLimit) goto page;
                    break;
                default:
                    if (index >= plan.Participants.Count) { exhausted = true; continue; }
                    var participant = plan.Participants[index];
                    if (rows > 0 && rows >= RowLimit) goto page;
                    participants.Add(participant);
                    index++;
                    rows++;
                    break;
            }
        }
        page:
        var page = plan with
        {
            Snapshots = snapshots, Frames = frames, Deliveries = deliveries,
            Participants = participants, Reads = null
        };
        RecoveryPageToken? next = exhausted ? null : new RecoveryPageToken(
            cursor.Depth, cursor.Epoch, cursor.Head, stage, index);
        return (page, next);
    }
}

using System.Text;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// The bridge's durable pending lane for ingress posts (review C3): a received WSS
/// message is never dropped because one HTTP post failed. Every post that does not
/// answer success is written as one spool file; the recovery pass re-posts the oldest
/// files first and deletes each one whose post lands. Redelivery may duplicate — the
/// shared face's dedup namespace answers replays, so ordering is attempted, never
/// guaranteed.
/// </summary>
public sealed class BilibiliIngressSpool
{
    private const string SpoolExtension = ".pending";

    private readonly string _directory;
    private long _sequence;

    public BilibiliIngressSpool(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>Persists one payload. The file name carries a millisecond stamp, a
    /// per-instance sequence and a guid: ordinal order is oldest-first, same-millisecond
    /// saves keep their call order, and concurrent saves never collide.</summary>
    public string Save(string json)
    {
        var path = Path.Combine(_directory,
            $"bili-ingress-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{Interlocked.Increment(ref _sequence):x6}-{Guid.NewGuid():N}{SpoolExtension}");
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }

    /// <summary>The oldest pending payloads, up to <paramref name="max"/>. A file that has
    /// vanished (a concurrent pass already landed it) is skipped, not an error.</summary>
    public IReadOnlyList<(string Path, string Json)> Take(int max)
    {
        var pending = new List<(string Path, string Json)>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*" + SpoolExtension)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (pending.Count >= max)
            {
                break;
            }
            try
            {
                pending.Add((path, File.ReadAllText(path, Encoding.UTF8)));
            }
            catch (IOException)
            {
                // Lost the race with another pass; the winner deletes it.
            }
        }
        return pending;
    }

    public void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A later pass re-takes and re-posts it; the dedup namespace answers the replay.
        }
    }

    public int Count()
        => Directory.EnumerateFiles(_directory, "*" + SpoolExtension).Count();
}

using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace Chorus.Rpc;

/// <summary>Remembers identities accepted by the HTTPS handshake for the life of a server process.</summary>
public sealed class TrustedProcessCertificates
{
    private sealed record Entry(ProcessIdentity Identity, DateTime NotAfterUtc);
    private readonly ConcurrentDictionary<string, Entry> _identities = new(StringComparer.Ordinal);

    public bool ValidateAndRemember(X509Certificate2? certificate, X509Certificate2 root,
        string clusterId, IEnumerable<X509Certificate2> intermediates)
    {
        if (certificate is null) return false;
        var identity = ProcessCertificateValidator.Validate(certificate, root, clusterId, intermediates);
        if (identity is null) return false;
        _identities[certificate.Thumbprint] = new Entry(identity, certificate.NotAfter.ToUniversalTime());
        if (_identities.Count > 2048)
        {
            var now = DateTime.UtcNow;
            foreach (var stale in _identities.Where(pair => pair.Value.NotAfterUtc <= now))
                _identities.TryRemove(stale.Key, out _);
        }
        return true;
    }

    public ProcessIdentity? Resolve(X509Certificate2 certificate) =>
        _identities.TryGetValue(certificate.Thumbprint, out var entry) &&
        entry.NotAfterUtc > DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow
            ? entry.Identity : null;
}

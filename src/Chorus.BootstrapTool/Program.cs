using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Rpc;

if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: Chorus.BootstrapTool <output-dir> <cluster-id> <node-id> <control-dll> <data-dll>");
    return 2;
}
var rootPassword = Environment.GetEnvironmentVariable("CHORUS_ROOT_PFX_PASSWORD");
var nodePassword = Environment.GetEnvironmentVariable("CHORUS_NODE_AUTHORITY_PFX_PASSWORD");
if (string.IsNullOrEmpty(rootPassword) || string.IsNullOrEmpty(nodePassword))
{
    Console.Error.WriteLine("Set CHORUS_ROOT_PFX_PASSWORD and CHORUS_NODE_AUTHORITY_PFX_PASSWORD.");
    return 2;
}
var output = Path.GetFullPath(args[0]);
var offline = Path.Combine(output, "offline");
var node = Path.Combine(output, "node");
Directory.CreateDirectory(offline);
Directory.CreateDirectory(node);
// Join mode (lab clusters): provision this node under an existing cluster root and node
// authority instead of minting a fresh trust domain — the node joins the trust graph the
// rest of the lab already validates. The joined authority's grant must name this node.
using var root = Environment.GetEnvironmentVariable("CHORUS_ROOT_PFX_FILE") is { Length: > 0 } rootPfx
    ? X509CertificateLoader.LoadPkcs12FromFile(rootPfx, rootPassword, X509KeyStorageFlags.EphemeralKeySet)
    : ProcessCertificateIssuer.CreateRoot(args[1], DateTimeOffset.UtcNow.AddYears(3));
string[] roles;
string[] tenants;
X509Certificate2 nodeAuthority;
if (Environment.GetEnvironmentVariable("CHORUS_NODE_AUTHORITY_PFX_FILE") is { Length: > 0 } joinPfx)
{
    nodeAuthority = X509CertificateLoader.LoadPkcs12FromFile(joinPfx, nodePassword,
        X509KeyStorageFlags.EphemeralKeySet);
    var joinedGrant = nodeAuthority.Extensions[ProcessCertificateValidator.NodeGrantOid] is { } extension
        ? JsonSerializer.Deserialize<NodeCertificateGrant>(extension.RawData)
        : null;
    if (joinedGrant is null ||
        !string.Equals(joinedGrant.ClusterId, args[1], StringComparison.Ordinal) ||
        !string.Equals(joinedGrant.NodeId, args[2], StringComparison.Ordinal))
        throw new InvalidOperationException("The joined node authority does not grant this cluster and node.");
    roles = joinedGrant.Roles;
    tenants = joinedGrant.Tenants;
}
else
{
    roles = (Environment.GetEnvironmentVariable("CHORUS_NODE_ROLES") ??
        "node-agent,control-server,data-server").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    tenants = (Environment.GetEnvironmentVariable("CHORUS_NODE_TENANTS") ?? "@cluster")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    nodeAuthority = ProcessCertificateIssuer.CreateNodeAuthority(root,
        new NodeCertificateGrant(args[1], args[2], roles, tenants), DateTimeOffset.UtcNow.AddYears(1));
}

var nodePrivate = Path.Combine(node, "node-ca.pfx");
// The offline root private key only exists when this run minted the cluster root; a joined
// node must never receive the cluster root private key.
if (Environment.GetEnvironmentVariable("CHORUS_ROOT_PFX_FILE") is not { Length: > 0 })
    File.WriteAllBytes(Path.Combine(offline, "root.pfx"), root.Export(X509ContentType.Pkcs12, rootPassword));
File.WriteAllBytes(nodePrivate, nodeAuthority.Export(X509ContentType.Pkcs12, nodePassword));
File.WriteAllText(Path.Combine(node, "root-ca.pem"), root.ExportCertificatePem());
File.WriteAllText(Path.Combine(node, "node-ca.pem"), nodeAuthority.ExportCertificatePem());
if (!OperatingSystem.IsWindows())
{
    File.SetUnixFileMode(offline, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    File.SetUnixFileMode(node, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    if (File.Exists(Path.Combine(offline, "root.pfx")))
        File.SetUnixFileMode(Path.Combine(offline, "root.pfx"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    File.SetUnixFileMode(nodePrivate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}

BootstrapSeedSlot Seed(string role, string assembly, int port)
{
    var path = Path.GetFullPath(assembly);
    if (!File.Exists(path)) throw new FileNotFoundException("Bootstrap assembly is missing.", path);
    return new BootstrapSeedSlot($"{args[2]}/{role}", role, path,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), port);
}
// The seed set is control + data (architecture 10.1); a game-server seed is optional lab
// topology so one node can host the whole match path under agent supervision.
var seeds = new List<BootstrapSeedSlot>
{
    Seed("control-server", args[3], Port("CHORUS_CONTROL_PORT", 7500)),
    Seed("data-server", args[4], Port("CHORUS_DATA_PORT", 7501))
};
if (Environment.GetEnvironmentVariable("CHORUS_GAME_DLL") is { Length: > 0 } gameDll)
    seeds.Add(Seed("game-server", gameDll, Port("CHORUS_GAME_PORT", 7503)));
var manifest = new BootstrapManifestPayload(args[1], args[2], "root-ca.pem", "node-ca.pem", "node-ca.pfx",
    $"https://localhost:{Environment.GetEnvironmentVariable("CHORUS_CONTROL_PORT") ?? "7500"}",
    DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
    seeds.ToArray());
File.WriteAllText(Path.Combine(node, "bootstrap.json"), JsonSerializer.Serialize(BootstrapManifest.Sign(manifest, root)));
Console.WriteLine(Environment.GetEnvironmentVariable("CHORUS_ROOT_PFX_FILE") is { Length: > 0 }
    ? "Joined an existing cluster root; node bundle: node/."
    : "Offline root private key: offline/root.pfx. Keep it outside the node. Node bundle: node/.");
return 0;

static int Port(string name, int fallback)
{
    var value = int.Parse(Environment.GetEnvironmentVariable(name) ?? fallback.ToString(),
        System.Globalization.CultureInfo.InvariantCulture);
    return value is > 0 and <= 65535 ? value : throw new ArgumentOutOfRangeException(name);
}

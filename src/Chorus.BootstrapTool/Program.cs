using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Redesign.Rpc;

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
using var root = ProcessCertificateIssuer.CreateRoot(args[1], DateTimeOffset.UtcNow.AddYears(3));
var roles = (Environment.GetEnvironmentVariable("CHORUS_NODE_ROLES") ??
    "node-agent,control-server,data-server").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var tenants = (Environment.GetEnvironmentVariable("CHORUS_NODE_TENANTS") ?? "@cluster")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var grant = new NodeCertificateGrant(args[1], args[2], roles, tenants);
using var nodeAuthority = ProcessCertificateIssuer.CreateNodeAuthority(root, grant, DateTimeOffset.UtcNow.AddYears(1));

var rootPrivate = Path.Combine(offline, "root.pfx");
var nodePrivate = Path.Combine(node, "node-ca.pfx");
File.WriteAllBytes(rootPrivate, root.Export(X509ContentType.Pkcs12, rootPassword));
File.WriteAllBytes(nodePrivate, nodeAuthority.Export(X509ContentType.Pkcs12, nodePassword));
File.WriteAllText(Path.Combine(node, "root-ca.pem"), root.ExportCertificatePem());
File.WriteAllText(Path.Combine(node, "node-ca.pem"), nodeAuthority.ExportCertificatePem());
if (!OperatingSystem.IsWindows())
{
    File.SetUnixFileMode(offline, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    File.SetUnixFileMode(node, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    File.SetUnixFileMode(rootPrivate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    File.SetUnixFileMode(nodePrivate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}

BootstrapSeedSlot Seed(string role, string assembly, int port)
{
    var path = Path.GetFullPath(assembly);
    if (!File.Exists(path)) throw new FileNotFoundException("Bootstrap assembly is missing.", path);
    return new BootstrapSeedSlot($"{args[2]}/{role}", role, path,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), port);
}
var manifest = new BootstrapManifestPayload(args[1], args[2], "root-ca.pem", "node-ca.pem", "node-ca.pfx",
    $"https://localhost:{Environment.GetEnvironmentVariable("CHORUS_CONTROL_PORT") ?? "7500"}",
    DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
    [Seed("control-server", args[3], Port("CHORUS_CONTROL_PORT", 7500)),
        Seed("data-server", args[4], Port("CHORUS_DATA_PORT", 7501))]);
File.WriteAllText(Path.Combine(node, "bootstrap.json"), JsonSerializer.Serialize(BootstrapManifest.Sign(manifest, root)));
Console.WriteLine("Offline root private key: offline/root.pfx. Keep it outside the node. Node bundle: node/.");
return 0;

static int Port(string name, int fallback)
{
    var value = int.Parse(Environment.GetEnvironmentVariable(name) ?? fallback.ToString(),
        System.Globalization.CultureInfo.InvariantCulture);
    return value is > 0 and <= 65535 ? value : throw new ArgumentOutOfRangeException(name);
}

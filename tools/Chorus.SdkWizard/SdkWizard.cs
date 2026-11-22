using System.Text.Json;

namespace Chorus.SdkWizard;

/// <summary>
/// The App configuration and material check wizard (FIX-U3D-04). Audits one Unity
/// project as files on disk, so it runs anywhere the repo runs — no Unity Editor
/// needed — and answers one verdict:
///
/// 1. The package dependencies carry the Chorus SDK and the selected platform's
///    package — the platform adapter assembly only compiles when its package is
///    present, because the versionDefine fires on the package name (FIX-U3D-03).
/// 2. The configuration surface is the public set and nothing else (layer 3,
///    FIX-U3D-01): complete, well-valued, and free of unknown fields.
/// 3. No secret-shaped value sits anywhere under Assets — app_secret, private keys
///    and access tokens are server-side configuration and refuse a build.
/// 4. The platform materials hold the shapes the platforms publish: the Kuaishou
///    ks_manifest.json rules (the launcher must be the appId exe at the package
///    root, capture parameters with the right classes) from the client access page.
///
/// A pass ends with the platform-side steps that still gate real traffic. A filled
/// appId is never presented as a review waiver — the admission matrix's honest
/// reading is that the code work can finish first and the platform steps cannot.
/// </summary>
public static class SdkWizardRunner
{
    /// <summary>The packages the wizard requires, per platform (FIX-U3D-01/03 pinned names).</summary>
    private static readonly Dictionary<string, string> PlatformPackages = new()
    {
        ["douyin"] = "com.bytedance.liveopensdk",
        ["kuaishou"] = "com.chorus.kuaishou.ipc",
    };

    /// <summary>The versionDefine each platform package fires (FIX-U3D-03).</summary>
    private static readonly Dictionary<string, string> PlatformSymbols = new()
    {
        ["douyin"] = "CHORUS_PLATFORM_DOUYIN",
        ["kuaishou"] = "CHORUS_PLATFORM_KUAISHOU",
    };

    /// <summary>The public configuration surface (layer 3): exactly these fields, no others.</summary>
    private static readonly string[] ConfigurationFields =
        ["platform", "appId", "tenant", "gameId", "endpoint", "role"];

    /// <summary>Substrings whose presence under Assets means a secret-shaped value. Case-insensitive.</summary>
    private static readonly string[] SecretPatterns =
        ["app_secret", "appsecret", "-----begin", "access_token", "accesstoken",
            "client_secret", "secret_key", "private_key", "privatekey"];

    private static readonly string[] Roles = ["streamer", "player"];

    public static int Run(TextWriter output, string projectRoot)
    {
        if (!Directory.Exists(projectRoot))
        {
            output.WriteLine($"the path is not a Unity project directory: {projectRoot}");
            return 2;
        }

        var errors = new List<string>();
        var configuration = CheckConfiguration(output, projectRoot, errors);
        CheckPackages(projectRoot, configuration, errors);
        ScanForSecrets(projectRoot, errors);
        var platform = configuration.TryGetValue("platform", out var value) ? value : null;
        if (platform == "kuaishou") CheckKuaishouMaterials(projectRoot, errors);

        if (errors.Count > 0)
        {
            foreach (var error in errors) output.WriteLine(error);
            output.WriteLine("the wizard refuses this build: fix the errors above.");
            return 1;
        }

        output.WriteLine($"PASS — the Unity project carries the Chorus SDK cleanly (platform: {platform}).");
        WriteAdvisory(output, platform!);
        return 0;
    }

    /// <summary>The public configuration surface: exactly one config file, exactly the known
    /// fields, every field well-valued. The pinned surface is what makes layer 3 checkable.</summary>
    private static Dictionary<string, string> CheckConfiguration(
        TextWriter output, string projectRoot, List<string> errors)
    {
        var assets = Path.Combine(projectRoot, "Assets");
        var files = Directory.Exists(assets)
            ? Directory.EnumerateFiles(assets, "chorus-sdk-config.json", SearchOption.AllDirectories).ToList()
            : [];
        if (files.Count == 0)
        {
            errors.Add("E config: no Assets/**/chorus-sdk-config.json — the game must carry its public configuration asset.");
            return [];
        }
        if (files.Count > 1)
        {
            errors.Add($"E config: {files.Count} configuration files found — exactly one chorus-sdk-config.json must exist.");
        }

        var path = files[0];
        Dictionary<string, JsonElement> document;
        try
        {
            document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
                ?? throw new JsonException("null document");
        }
        catch (JsonException error)
        {
            errors.Add($"E config: {Relative(projectRoot, path)} is not valid JSON: {error.Message}");
            return [];
        }

        foreach (var field in document.Keys.Except(ConfigurationFields, StringComparer.Ordinal))
            errors.Add($"E config: {Relative(projectRoot, path)} carries unknown field '{field}' — the public surface is pinned; secrets belong to server configuration.");

        var values = new Dictionary<string, string>();
        foreach (var field in ConfigurationFields)
        {
            if (!document.TryGetValue(field, out var element)
                || element.ValueKind != JsonValueKind.String
                || element.GetString() is not { Length: > 0 } text)
            {
                errors.Add($"E config: {Relative(projectRoot, path)} misses '{field}' or leaves it empty.");
                continue;
            }
            values[field] = text;
        }
        if (values.TryGetValue("platform", out var platform) && !PlatformPackages.ContainsKey(platform))
            errors.Add($"E config: platform '{platform}' is not one of {string.Join(", ", PlatformPackages.Keys)}.");
        if (values.TryGetValue("role", out var role) && !Roles.Contains(role, StringComparer.Ordinal))
            errors.Add($"E config: role '{role}' is not one of {string.Join(", ", Roles)}.");
        if (values.TryGetValue("endpoint", out var endpoint) && !endpoint.StartsWith("https://", StringComparison.Ordinal))
            errors.Add($"E config: endpoint '{endpoint}' is not an https public endpoint.");
        return values;
    }

    /// <summary>The package dependencies: the SDK itself and the selected platform's package —
    /// without it the versionDefine never defines the symbol and the adapter never compiles.</summary>
    private static void CheckPackages(
        string projectRoot, Dictionary<string, string> configuration, List<string> errors)
    {
        var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            errors.Add("E package: no Packages/manifest.json — the project has no Unity package manifest.");
            return;
        }
        Dictionary<string, JsonElement> dependencies;
        try
        {
            dependencies = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(manifestPath))
                ?.GetValueOrDefault("dependencies") is JsonElement element
                && element.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(element.GetRawText())
                : null;
        }
        catch (JsonException error)
        {
            errors.Add($"E package: Packages/manifest.json is not valid JSON: {error.Message}");
            return;
        }
        dependencies ??= [];

        if (!dependencies.ContainsKey("com.chorus.sdk"))
            errors.Add("E package: Packages/manifest.json does not depend on com.chorus.sdk.");

        if (configuration.TryGetValue("platform", out var platform)
            && PlatformPackages.TryGetValue(platform, out var required)
            && !dependencies.ContainsKey(required))
        {
            errors.Add($"E package: platform '{platform}' needs the package '{required}' in Packages/manifest.json — " +
                $"without it the versionDefine never defines {PlatformSymbols[platform]} and the platform adapter never compiles.");
        }
    }

    /// <summary>Layer 3 (FIX-U3D-01): no secret-shaped value anywhere under Assets. The scan is
    /// deliberately strict — a refusal costs a moved line, a leak costs the application.</summary>
    private static void ScanForSecrets(string projectRoot, List<string> errors)
    {
        var assets = Path.Combine(projectRoot, "Assets");
        if (!Directory.Exists(assets)) return;
        foreach (var path in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            if (extension is not (".cs" or ".json" or ".asset")) continue;
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var pattern in SecretPatterns)
                {
                    if (!lines[index].Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
                    errors.Add($"E secret: {Relative(projectRoot, path)}:{index + 1} carries a secret-shaped value " +
                        $"('{pattern}') — server-side configuration only; move it out of the Unity project (layer 3).");
                }
            }
        }
    }

    /// <summary>The Kuaishou package materials (client access page, updated 2024-11-22): the
    /// launcher must be the appId exe at the package root, the capture parameters must name
    /// real window classes for game/window capture, and the IPC flags must be booleans.</summary>
    private static void CheckKuaishouMaterials(string projectRoot, List<string> errors)
    {
        var assets = Path.Combine(projectRoot, "Assets");
        var files = Directory.Exists(assets)
            ? Directory.EnumerateFiles(assets, "ks_manifest.json", SearchOption.AllDirectories).ToList()
            : [];
        if (files.Count == 0)
        {
            errors.Add("E kuaishou: no Assets/**/ks_manifest.json — the companion cannot launch the game without its package manifest.");
            return;
        }
        var path = files[0];
        Dictionary<string, JsonElement> manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
                ?? throw new JsonException("null document");
        }
        catch (JsonException error)
        {
            errors.Add($"E kuaishou: {Relative(projectRoot, path)} is not valid JSON: {error.Message}");
            return;
        }

        if (!manifest.TryGetValue("launcherFileName", out var launcher) || launcher.ValueKind != JsonValueKind.String)
        {
            errors.Add($"E kuaishou: {Relative(projectRoot, path)} misses 'launcherFileName'.");
        }
        else if (!System.Text.RegularExpressions.Regex.IsMatch(launcher.GetString() ?? "", "^ks[0-9]+\\.exe$"))
        {
            errors.Add($"E kuaishou: launcherFileName '{launcher.GetString()}' is not the appId exe — the platform requires the launcher to be named ks<appId>.exe at the package root.");
        }

        foreach (var flag in (string[])["ipcSetCode", "ipcQuit"])
        {
            if (manifest.TryGetValue(flag, out var value) && value.ValueKind != JsonValueKind.True
                && value.ValueKind != JsonValueKind.False)
                errors.Add($"E kuaishou: '{flag}' must be a boolean (the -ipc launch parameter rides it).");
        }

        if (!manifest.TryGetValue("captureParam", out var captures) || captures.ValueKind != JsonValueKind.Array
            || captures.GetArrayLength() == 0)
        {
            errors.Add($"E kuaishou: {Relative(projectRoot, path)} misses 'captureParam' or leaves it empty — the companion captures the game through it.");
            return;
        }
        var index = 0;
        foreach (var capture in captures.EnumerateArray())
        {
            index++;
            var type = capture.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type is not ("game" or "window" or "camera"))
            {
                errors.Add($"E kuaishou: captureParam[{index}] type '{type}' is not one of game, window, camera.");
                continue;
            }
            var needsWindowClass = type is "game" or "window";
            var hasWindowClass = capture.TryGetProperty("wndClass", out var windowClass)
                && windowClass.ValueKind == JsonValueKind.String
                && windowClass.GetString() is { Length: > 0 };
            if (needsWindowClass && !hasWindowClass)
                errors.Add($"E kuaishou: captureParam[{index}] of type '{type}' needs 'wndClass' (Unity games use UnityWndClass).");
            if (type == "camera"
                && (!capture.TryGetProperty("deviceId", out var device) || device.GetString() is not { Length: > 0 }))
                errors.Add($"E kuaishou: captureParam[{index}] of type 'camera' needs 'deviceId'.");
        }
    }

    /// <summary>The pass verdict: what the platform side still gates. From the admission
    /// matrix (FIX-U3D-01) — the wizard's success message never waives review.</summary>
    private static void WriteAdvisory(TextWriter output, string platform)
    {
        output.WriteLine("Open platform-side steps that still gate real traffic:");
        if (platform == "douyin")
        {
            output.WriteLine("- capability activation: the Apply menu's internal-test grant and the per-capability opening in the authorized console");
            output.WriteLine("- gift allowlist configuration and the live event subscription");
            output.WriteLine("- the server-side fulfillment ACK integration (the platform rejects review without it)");
            output.WriteLine("- proposal, package and review materials in the authorized console");
        }
        else
        {
            output.WriteLine("- the interface scope grant before joint debugging");
            output.WriteLine("- the gift subscription list (only subscribed gifts are pushed)");
            output.WriteLine("- the launch-required server trio: bind, gift top, interaction-message push (not integrated, cannot go live)");
            output.WriteLine("- package upload and review materials in the authorized console");
        }
        output.WriteLine("A filled appId is not a review waiver: every step above still gates real traffic.");
    }

    private static string Relative(string projectRoot, string path) =>
        Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
}

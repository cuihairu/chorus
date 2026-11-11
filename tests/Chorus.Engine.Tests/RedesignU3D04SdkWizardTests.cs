using System.Text;
using Chorus.SdkWizard;

namespace Chorus.Engine.Tests;

/// <summary>
/// The SDK configuration and material check wizard (FIX-U3D-04), driven end to end
/// against synthesized Unity project trees: a complete project passes and carries the
/// platform-side advisory (never a review waiver), a missing platform package, an
/// incomplete or unknown-fielded configuration, a secret-shaped value under Assets
/// and a malformed Kuaishou manifest are each refused by name. The fixtures are real
/// file trees in temp directories — the wizard audits files, so the tests feed it files.
/// </summary>
public sealed class RedesignU3D04SdkWizardTests
{
    [Fact]
    public void ACompleteDouyinProjectPassesWithThePlatformAdvisory()
    {
        var root = ProjectDirectory();
        WriteAllText(root, "Packages/manifest.json", Dependencies(
            "com.unity.ugui", "1.0.0", "com.chorus.sdk", "0.1.0", "com.bytedance.liveopensdk", "2.7.4"));
        WriteAllText(root, "Assets/Game/chorus-sdk-config.json", Configuration("douyin"));
        WriteAllText(root, "Assets/Scripts/Game.cs", "public class Game { }\n");

        var report = Output();
        Assert.Equal(0, SdkWizardRunner.Run(report, root));
        Assert.Contains("PASS", report.ToString());
        Assert.Contains("fulfillment ACK", report.ToString());
        // The honest verdict: the wizard's pass never waives review.
        Assert.Contains("not a review waiver", report.ToString());
    }

    [Fact]
    public void ACompleteKuaishouProjectPasses()
    {
        var root = ProjectDirectory();
        WriteAllText(root, "Packages/manifest.json", Dependencies(
            "com.chorus.sdk", "0.1.0", "com.chorus.kuaishou.ipc", "0.1.0"));
        WriteAllText(root, "Assets/Game/chorus-sdk-config.json", Configuration("kuaishou"));
        WriteAllText(root, "Assets/Game/ks_manifest.json", """
{
  "launcherFileName": "ks700591218773076711.exe",
  "workFileNames": ["Game.exe"],
  "ipcSetCode": false,
  "ipcQuit": true,
  "captureParam": [
    { "captureCursor": true, "type": "game", "wndName": "game", "wndClass": "UnityWndClass", "name": "game.exe" }
  ]
}
""");

        var report = Output();
        Assert.Equal(0, SdkWizardRunner.Run(report, root));
        Assert.Contains("launch-required", report.ToString());
    }

    [Fact]
    public void AMissingPlatformPackageIsNamedAndRefused()
    {
        var root = ProjectDirectory();
        // The platform package is absent: the versionDefine never fires and the adapter
        // never compiles — the refusal must name both the package and the symbol.
        WriteAllText(root, "Packages/manifest.json", Dependencies(
            "com.unity.ugui", "1.0.0", "com.chorus.sdk", "0.1.0"));
        WriteAllText(root, "Assets/Game/chorus-sdk-config.json", Configuration("douyin"));

        var report = Output();
        Assert.Equal(1, SdkWizardRunner.Run(report, root));
        Assert.Contains("com.bytedance.liveopensdk", report.ToString());
        Assert.Contains("CHORUS_PLATFORM_DOUYIN", report.ToString());
    }

    [Fact]
    public void AnIncompleteConfigIsRefusedByField()
    {
        var root = ProjectDirectory();
        WriteAllText(root, "Packages/manifest.json", Dependencies("com.chorus.sdk", "0.1.0", "com.chorus.kuaishou.ipc", "0.1.0"));
        WriteAllText(root, "Assets/Game/chorus-sdk-config.json", """
{
  "platform": "kuaishou",
  "appId": "ks700591218773076711",
  "gameId": "game-a",
  "endpoint": "https://chorus.example.com",
  "role": "spectator"
}
""");

        var report = Output();
        Assert.Equal(1, SdkWizardRunner.Run(report, root));
        Assert.Contains("'tenant'", report.ToString());
        Assert.Contains("'spectator'", report.ToString());
    }

    [Fact]
    public void AnUnknownConfigFieldIsRefused()
    {
        var root = ProjectDirectory();
        WriteAllText(root, "Packages/manifest.json", Dependencies("com.chorus.sdk", "0.1.0", "com.bytedance.liveopensdk", "2.7.4"));
        WriteAllText(root, "Assets/Game/chorus-sdk-config.json", Configuration("douyin")
            .Replace("{", """
{
  "appSecret": "server-side-only",
"""));

        var report = Output();
        Assert.Equal(1, SdkWizardRunner.Run(report, root));
        Assert.Contains("unknown field 'appSecret'", report.ToString());
        // The unknown-field refusal is the layer-3 front line — the same occurrence is
        // also caught by the secret scan.
        Assert.Contains("secret-shaped", report.ToString());
    }

    [Fact]
    public void ASecretShapedValueUnderAssetsIsRefused()
    {
        var root = ProjectDirectory();
        WriteAllText(root, "Packages/manifest.json", Dependencies("com.chorus.sdk", "0.1.0", "com.chorus.kuaishou.ipc", "0.1.0"));
        WriteAllText(root, "Assets/Game/chorus-sdk-config.json", Configuration("kuaishou"));
        WriteAllText(root, "Assets/Game/ks_manifest.json", ValidKuaishouManifest());
        WriteAllText(root, "Assets/Scripts/Net.cs",
            "public class Net { private const string AppSecret = \"the-real-one\"; }\n");

        var report = Output();
        Assert.Equal(1, SdkWizardRunner.Run(report, root));
        Assert.Contains("Assets/Scripts/Net.cs", report.ToString());
        Assert.Contains("appsecret", report.ToString().ToLowerInvariant());
    }

    [Fact]
    public void AMalformedKuaishouManifestIsRefusedByField()
    {
        var root = ProjectDirectory();
        WriteAllText(root, "Packages/manifest.json", Dependencies("com.chorus.sdk", "0.1.0", "com.chorus.kuaishou.ipc", "0.1.0"));
        WriteAllText(root, "Assets/Game/chorus-sdk-config.json", Configuration("kuaishou"));
        WriteAllText(root, "Assets/Game/ks_manifest.json", ValidKuaishouManifest()
            .Replace("ks700591218773076711.exe", "game.exe"));

        var report = Output();
        Assert.Equal(1, SdkWizardRunner.Run(report, root));
        Assert.Contains("launcherFileName", report.ToString());
    }

    [Fact]
    public void APathThatIsNoUnityProjectAnswersUsage()
    {
        Assert.Equal(2, SdkWizardRunner.Run(Output(), Path.Combine(Path.GetTempPath(), "no-such-unity-project")));
    }

    /// <summary>The Kuaishou manifest every kuaishou fixture builds on.</summary>
    private static string ValidKuaishouManifest() => """
{
  "launcherFileName": "ks700591218773076711.exe",
  "ipcSetCode": false,
  "ipcQuit": false,
  "captureParam": [
    { "captureCursor": true, "type": "game", "wndName": "game", "wndClass": "UnityWndClass", "name": "game.exe" }
  ]
}
""";

    private static string Configuration(string platform) => $$"""
{
  "platform": "{{platform}}",
  "appId": "{{(platform == "douyin" ? "tt-app-1" : "ks700591218773076711")}}",
  "tenant": "tenant-a",
  "gameId": "game-a",
  "endpoint": "https://chorus.example.com",
  "role": "streamer"
}
""";

    private static string Dependencies(params string[] pairs) =>
        "{\n  \"dependencies\": {\n" + string.Join(",\n",
            pairs.Chunk(2).Select(pair => $"    \"{pair[0]}\": \"{pair[1]}\"")) + "\n  }\n}\n";

    private static string ProjectDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "sdk-wizard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteAllText(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static StringWriter Output() => new();
}

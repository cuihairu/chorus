using System.Text.Json;
using Chorus.UnitySdk;

namespace Chorus.Engine.Tests;

/// <summary>
/// The Unity package and its conditional-compilation isolation (FIX-U3D-03): the
/// contract source folder doubles as the UPM package (the same files a dotnet build
/// compiles are the files Unity imports), with one neutral Runtime assembly and one
/// folder per platform, each platform assembly kept out of the compile until its
/// symbol exists — the Douyin symbol arrives automatically with the official
/// LiveOpenSDK package (the managed-assembly shape the admission matrix pinned), the
/// Kuaishou assembly additionally compiles on Windows targets only (the companion
/// IPC's native shape) and its symbol rides the Chorus IPC wrapper package (FIX-U3D-06
/// delivers it under the name pinned here). The tests parse the package files as JSON
/// from the tree, so a drift — a symbol rename, a loosened constraint, a platform
/// namespace leaking into the neutral sources — fails before any Unity import.
/// </summary>
public sealed class RedesignU3D03UnityPackageIsolationTests
{
    [Fact]
    public void ThePackageManifestPinsTheUnityFloor()
    {
        using var document = JsonDocument.Parse(ReadPackageFile("package.json"));
        var manifest = document.RootElement;
        Assert.Equal("com.chorus.sdk", manifest.GetProperty("name").GetString());
        // The floor the admission matrix pinned (FIX-U3D-01): Douyin's LiveOpenSDK needs
        // Unity 2020+ LTS — a lower floor fails a streamer's import, a higher one drops
        // supported streamers, so it is pinned here.
        Assert.Equal("2020.3", manifest.GetProperty("unity").GetString());
        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("description").GetString()));
    }

    [Fact]
    public void TheNeutralRuntimeAssemblyReferencesNoPlatform()
    {
        var runtime = JsonDocument.Parse(ReadPackageFile(
            "Runtime/Chorus.UnitySdk.asmdef")).RootElement;
        Assert.Equal("Chorus.UnitySdk", runtime.GetProperty("name").GetString());
        Assert.Equal([], Enumerate(runtime.GetProperty("references")));
        Assert.Equal([], Enumerate(runtime.GetProperty("defineConstraints")));
        Assert.Equal([], Enumerate(runtime.GetProperty("versionDefines")));
        // The neutral assembly compiles everywhere and stays out of the platform seams.
        Assert.Equal([], Enumerate(runtime.GetProperty("includePlatforms")));
        Assert.Equal([], Enumerate(runtime.GetProperty("excludePlatforms")));
        Assert.False(runtime.GetProperty("allowUnsafeCode").GetBoolean());
    }

    [Fact]
    public void TheDouyinIsolationCompilesOnlyWhenTheOfficialSdkIsPresent()
    {
        var douyin = JsonDocument.Parse(ReadPackageFile(
            "Platforms/Douyin/Chorus.UnitySdk.Platforms.Douyin.asmdef")).RootElement;
        Assert.Equal(["CHORUS_PLATFORM_DOUYIN"], Enumerate(douyin.GetProperty("defineConstraints")));
        // The managed-assembly shape (FIX-U3D-01): the symbol is defined automatically
        // the moment the official SDK's UPM package is in the project — installing the
        // platform SDK is the whole opt-in.
        var defines = douyin.GetProperty("versionDefines").EnumerateArray().ToList();
        var entry = Assert.Single(defines);
        Assert.Equal("com.bytedance.liveopensdk", entry.GetProperty("name").GetString());
        Assert.Equal(string.Empty, entry.GetProperty("expression").GetString());
        Assert.Equal("CHORUS_PLATFORM_DOUYIN", entry.GetProperty("define").GetString());
    }

    [Fact]
    public void TheKuaishouIsolationIsWindowsOnlyAndKeyedOnTheIpcWrapper()
    {
        var kuaishou = JsonDocument.Parse(ReadPackageFile(
            "Platforms/Kuaishou/Chorus.UnitySdk.Platforms.Kuaishou.asmdef")).RootElement;
        Assert.Equal(["CHORUS_PLATFORM_KUAISHOU"], Enumerate(kuaishou.GetProperty("defineConstraints")));
        // The native shape (FIX-U3D-01): the companion IPC library is a Windows native
        // binary — the assembly must not compile for any other player target, and the
        // editor compiles it so Windows development machines still build.
        Assert.Equal(["Editor", "WindowsStandalone32", "WindowsStandalone64"],
            Enumerate(kuaishou.GetProperty("includePlatforms")));
        var defines = kuaishou.GetProperty("versionDefines").EnumerateArray().ToList();
        var entry = Assert.Single(defines);
        // The wrapper package FIX-U3D-06 will deliver the IPC library under; the name is
        // pinned here so both tasks cannot drift apart.
        Assert.Equal("com.chorus.kuaishou.ipc", entry.GetProperty("name").GetString());
        Assert.Equal("CHORUS_PLATFORM_KUAISHOU", entry.GetProperty("define").GetString());
    }

    [Fact]
    public void TheSourceTreeKeepsThePlatformsOutOfTheNeutralRuntime()
    {
        var root = FindRoot();
        var runtime = Path.Combine(root, "src", "Chorus.UnitySdk", "Runtime");
        foreach (var source in Directory.EnumerateFiles(runtime, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain("ByteDance", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kwai", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Platforms.", text, StringComparison.Ordinal);
        }
        // Every platform assembly definition sits in its own folder and nowhere else:
        // the folder is the isolation, the asmdef is the gate.
        var sdk = Path.Combine(root, "src", "Chorus.UnitySdk");
        Assert.Equal(
            ["Platforms/Douyin/Chorus.UnitySdk.Platforms.Douyin.asmdef",
                "Platforms/Kuaishou/Chorus.UnitySdk.Platforms.Kuaishou.asmdef",
                "Runtime/Chorus.UnitySdk.asmdef"],
            Directory.EnumerateFiles(sdk, "*.asmdef", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(sdk, path).Replace('\\', '/'))
                .Order()
                .ToList());
    }

    /// <summary>A JSON array of strings read back as a sorted list, so order never masks a drift.</summary>
    private static List<string> Enumerate(JsonElement array) =>
        array.EnumerateArray().Select(element => element.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string ReadPackageFile(string relativePath)
    {
        var root = FindRoot();
        return File.ReadAllText(Path.Combine(root, "src", "Chorus.UnitySdk", relativePath));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chorus.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

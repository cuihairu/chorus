using System.IO;
using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// Flame-graph capture contract. The real dotnet-trace is not a test
/// dependency: a stub shell script records argv and fabricates the outputs,
/// so the collect/convert flow, the output-path contract and every failure
/// path (missing tool, failed collect, hung collect) stay testable.
/// </summary>
public sealed class LoadToolFlameGraphTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "chorus-flamegraph-" + Guid.NewGuid().ToString("N"));

    public LoadToolFlameGraphTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TraceDurationSeconds_IsCappedAtRunDuration()
    {
        // The trace cannot outlive the run it profiles.
        Assert.Equal(10, FlameGraphProfiler.TraceDurationSeconds(
            new LoadToolOptions(FlameGraphEnabled: true, FlameGraphSeconds: 30, DurationSeconds: 10)));
        Assert.Equal(5, FlameGraphProfiler.TraceDurationSeconds(
            new LoadToolOptions(FlameGraphEnabled: true, FlameGraphSeconds: 5, DurationSeconds: 60)));
    }

    [Fact]
    public void TryStart_Disabled_IsPureNoOp()
    {
        var outDir = Path.Combine(_work, "out");
        var options = new LoadToolOptions(FlameGraphDir: outDir);

        var profiler = FlameGraphProfiler.TryStart(options, toolPath: ExistingTool());

        Assert.Null(profiler);
        Assert.False(Directory.Exists(outDir), "a disabled capture must not create the output directory");
    }

    [Fact]
    public void TryStart_MissingTool_ReturnsNullWithInstallGuidance()
    {
        var options = new LoadToolOptions(FlameGraphEnabled: true);

        // The guidance names the fix, not just the failure.
        Assert.Contains("dotnet tool install -g dotnet-trace", FlameGraphProfiler.MissingToolMessage);
        Assert.Null(FlameGraphProfiler.TryStart(options, toolPath: null));
        Assert.Null(FlameGraphProfiler.TryStart(options, toolPath: Path.Combine(_work, "no-such-dotnet-trace")));
    }

    [Fact]
    public async Task CompleteAsync_StubTool_CollectsAndConverts()
    {
        var stubLog = Path.Combine(_work, "stub-args.log");
        var stub = WriteStubTool("""
            #!/bin/sh
            echo "$@" >> "$FLAMEGRAPH_STUB_LOG"
            out=""
            prev=""
            for a in "$@"; do
              if [ "$prev" = "-o" ]; then out="$a"; fi
              prev="$a"
            done
            if [ "$1" = "collect" ]; then
              : > "$out"
            elif [ "$1" = "convert" ]; then
              touch "${2%.nettrace}.speedscope.json"
            fi
            """);
        var outDir = Path.Combine(_work, "out");
        var previous = SetStubLog(stubLog);
        try
        {
            var options = new LoadToolOptions(
                FlameGraphEnabled: true, FlameGraphSeconds: 30, DurationSeconds: 10, FlameGraphDir: outDir);
            var profiler = FlameGraphProfiler.TryStart(options, stub);
            Assert.NotNull(profiler);

            await profiler.CompleteAsync(TextWriter.Null, TimeSpan.FromSeconds(20));

            // Output-path contract: one nettrace named after this run inside
            // the requested directory, plus the converted speedscope sibling.
            var tracePath = Directory.GetFiles(outDir, "*.nettrace").Single();
            Assert.Contains($"_{Environment.ProcessId}.nettrace", Path.GetFileName(tracePath));
            Assert.Equal(Path.ChangeExtension(tracePath, ".speedscope.json"), profiler.SpeedscopePath);
            Assert.True(File.Exists(profiler.SpeedscopePath));

            var argv = await File.ReadAllLinesAsync(stubLog);
            var collect = argv.Single(line => line.StartsWith("collect", StringComparison.Ordinal));
            Assert.Contains("--profile cpu-sampling", collect);
            Assert.Contains($"--process-id {Environment.ProcessId}", collect);
            // 30s flame-graph window capped at the 10s run duration.
            Assert.Contains("--duration 10", collect);
            Assert.Contains($"-o {tracePath}", collect);
            var convert = argv.Single(line => line.StartsWith("convert", StringComparison.Ordinal));
            Assert.Equal($"convert {tracePath} --format speedscope", convert);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLAMEGRAPH_STUB_LOG", previous);
        }
    }

    [Fact]
    public async Task CompleteAsync_FailedCollect_SkipsConversionAndNeverThrows()
    {
        var stubLog = Path.Combine(_work, "stub-args.log");
        var stub = WriteStubTool("""
            #!/bin/sh
            echo "$@" >> "$FLAMEGRAPH_STUB_LOG"
            if [ "$1" = "collect" ]; then
              echo "stub trace failure" >&2
              exit 3
            fi
            """);
        var log = new StringWriter();
        var previous = SetStubLog(stubLog);
        try
        {
            var options = new LoadToolOptions(
                FlameGraphEnabled: true, FlameGraphSeconds: 5, DurationSeconds: 60,
                FlameGraphDir: Path.Combine(_work, "out"));
            var profiler = FlameGraphProfiler.TryStart(options, stub);
            Assert.NotNull(profiler);

            await profiler.CompleteAsync(log, TimeSpan.FromSeconds(20));

            var text = log.ToString();
            Assert.Contains("collect exit=3", text);
            Assert.Contains("stub trace failure", text);
            var argv = await File.ReadAllLinesAsync(stubLog);
            Assert.DoesNotContain(argv, line => line.StartsWith("convert", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLAMEGRAPH_STUB_LOG", previous);
        }
    }

    [Fact]
    public async Task CompleteAsync_HungCollect_TimesOutKillsAndNeverThrows()
    {
        var stub = WriteStubTool("""
            #!/bin/sh
            echo "$@" >> "$FLAMEGRAPH_STUB_LOG"
            if [ "$1" = "collect" ]; then
              sleep 60
            fi
            """);
        var log = new StringWriter();
        var previous = SetStubLog(Path.Combine(_work, "stub-args.log"));
        try
        {
            var options = new LoadToolOptions(
                FlameGraphEnabled: true, FlameGraphSeconds: 5, DurationSeconds: 60,
                FlameGraphDir: Path.Combine(_work, "out"));
            var profiler = FlameGraphProfiler.TryStart(options, stub);
            Assert.NotNull(profiler);

            // The bounded wait is the test's own timeout: a hung collector is
            // killed instead of hanging the load tool.
            await profiler.CompleteAsync(log, TimeSpan.FromSeconds(5));

            Assert.Contains("did not exit in time", log.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLAMEGRAPH_STUB_LOG", previous);
        }
    }

    private static string? SetStubLog(string stubLog)
    {
        var previous = Environment.GetEnvironmentVariable("FLAMEGRAPH_STUB_LOG");
        Environment.SetEnvironmentVariable("FLAMEGRAPH_STUB_LOG", stubLog);
        return previous;
    }

    private static string ExistingTool()
    {
        // A file that exists on every Unix CI runner; the disabled path never
        // spawns it, existence is all that matters.
        return "/bin/true";
    }

    private string WriteStubTool(string body)
    {
        var path = Path.Combine(_work, "dotnet-trace-stub.sh");
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Chorus.LoadTool;

/// <summary>
/// Optional CPU flame-graph capture around a load run: spawns an external
/// <c>dotnet-trace collect</c> (cpu-sampling profile) attached to this process,
/// then converts the nettrace to speedscope for https://speedscope.app.
/// Best-effort by contract: a missing tool or a failed capture/convert prints
/// guidance and never aborts the run or changes its exit code. Disabled (the
/// default) is a no-op with no side effects. Each driver shard profiles its
/// own process, so multi-process runs produce one trace per shard.
/// </summary>
public sealed class FlameGraphProfiler
{
    private readonly string _toolPath;
    private readonly Process _collect;
    private readonly Task<string> _collectStdout;
    private readonly Task<string> _collectStderr;

    private FlameGraphProfiler(string toolPath, Process collect, string tracePath)
    {
        _toolPath = toolPath;
        _collect = collect;
        TracePath = tracePath;
        _collectStdout = collect.StandardOutput.ReadToEndAsync();
        _collectStderr = collect.StandardError.ReadToEndAsync();
    }

    public string TracePath { get; }

    public string SpeedscopePath => Path.ChangeExtension(TracePath, ".speedscope.json");

    public int DurationSeconds { get; private set; }

    /// <summary>
    /// Trace window: the requested flame-graph seconds capped at the run
    /// duration (a trace cannot outlive the run it profiles).
    /// </summary>
    public static int TraceDurationSeconds(LoadToolOptions options) =>
        Math.Min(Math.Max(1, options.FlameGraphSeconds), Math.Max(1, options.DurationSeconds));

    public static FlameGraphProfiler? TryStart(LoadToolOptions options) =>
        TryStart(options, ResolveToolPath());

    /// <summary>Test seam: an explicit tool path (null/missing = guidance, null return).</summary>
    public static FlameGraphProfiler? TryStart(LoadToolOptions options, string? toolPath)
    {
        // Disabled must stay a pure no-op: no directory, no process, no output.
        if (!options.FlameGraphEnabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath))
        {
            Console.Error.WriteLine(MissingToolMessage);
            return null;
        }

        try
        {
            var duration = TraceDurationSeconds(options);
            Directory.CreateDirectory(options.FlameGraphDir);
            var tracePath = Path.Combine(options.FlameGraphDir,
                $"loadtool_{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}_{Environment.ProcessId}.nettrace");

            var startInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("collect");
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add("cpu-sampling");
            startInfo.ArgumentList.Add("--process-id");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--duration");
            startInfo.ArgumentList.Add(duration.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(tracePath);

            var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.Error.WriteLine(
                    "[loadtool] flamegraph: failed to start dotnet-trace; the run continues without a flame graph.");
                return null;
            }

            Console.WriteLine($"[loadtool] flamegraph: tracing pid={Environment.ProcessId} for {duration}s -> {tracePath}");
            return new FlameGraphProfiler(toolPath, process, tracePath) { DurationSeconds = duration };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[loadtool] flamegraph: capture failed ({ex.Message}); the run continues without a flame graph.");
            return null;
        }
    }

    /// <summary>
    /// Waits for the bounded collect window, then converts the nettrace to
    /// speedscope. Every failure is reported, never thrown.
    /// </summary>
    public async Task CompleteAsync(TextWriter log, TimeSpan? timeout = null)
    {
        try
        {
            var bound = timeout ?? TimeSpan.FromSeconds(DurationSeconds + 60);
            if (!await WaitForExitAsync(_collect, bound))
            {
                TryKill(_collect);
                await log.WriteLineAsync(
                    "[loadtool] flamegraph: dotnet-trace did not exit in time and was killed; no conversion was attempted.");
                return;
            }

            var stdout = await _collectStdout;
            var stderr = await _collectStderr;
            if (_collect.ExitCode != 0)
            {
                await log.WriteLineAsync(
                    $"[loadtool] flamegraph: collect exit={_collect.ExitCode}: {Tail(stderr, stdout)}");
                return;
            }

            if (!File.Exists(TracePath))
            {
                await log.WriteLineAsync(
                    $"[loadtool] flamegraph: no trace file at {TracePath}; skipping conversion.");
                return;
            }

            await ConvertAsync(log);
        }
        catch (Exception ex)
        {
            await log.WriteLineAsync(
                $"[loadtool] flamegraph: finishing failed ({ex.Message}); the run continues.");
        }
        finally
        {
            _collect.Dispose();
        }
    }

    private async Task ConvertAsync(TextWriter log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _toolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("convert");
        startInfo.ArgumentList.Add(TracePath);
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("speedscope");

        using var convert = Process.Start(startInfo);
        if (convert is null)
        {
            await log.WriteLineAsync(
                $"[loadtool] flamegraph: could not start the conversion; the raw trace stays at {TracePath}.");
            return;
        }

        var stdout = convert.StandardOutput.ReadToEndAsync();
        var stderr = convert.StandardError.ReadToEndAsync();
        if (!await WaitForExitAsync(convert, TimeSpan.FromSeconds(120)))
        {
            TryKill(convert);
            await log.WriteLineAsync(
                $"[loadtool] flamegraph: conversion timed out; the raw trace stays at {TracePath}.");
            return;
        }

        if (convert.ExitCode != 0)
        {
            await log.WriteLineAsync(
                $"[loadtool] flamegraph: convert exit={convert.ExitCode}: {Tail(await stderr, await stdout)}");
            return;
        }

        if (!File.Exists(SpeedscopePath))
        {
            await log.WriteLineAsync(
                $"[loadtool] flamegraph: conversion reported success but {SpeedscopePath} is missing.");
            return;
        }

        await log.WriteLineAsync($"[loadtool] flamegraph: speedscope {SpeedscopePath}");
        await log.WriteLineAsync(
            $"[loadtool] flamegraph: open {SpeedscopePath} at https://speedscope.app (raw trace: {TracePath}).");
    }

    /// <summary>
    /// Locates the dotnet-trace global tool: on PATH first, then the default
    /// per-user tools directory. Null means not installed.
    /// </summary>
    public static string? ResolveToolPath()
    {
        var exeName = OperatingSystem.IsWindows() ? "dotnet-trace.exe" : "dotnet-trace";

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(
                Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var tool = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", exeName);
        return File.Exists(tool) ? tool : null;
    }

    public static string MissingToolMessage =>
        "[loadtool] flamegraph: dotnet-trace was not found on PATH or in ~/.dotnet/tools.\n" +
        "[loadtool] flamegraph: install it once with `dotnet tool install -g dotnet-trace`, " +
        "then rerun with --flamegraph.\n" +
        "[loadtool] flamegraph: this run continues without a flame graph.";

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The tool is already gone or cannot be signalled; nothing to do.
        }
    }

    private static string Tail(params string[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Length > 400 ? p[..400] + "…" : p.Trim()));
}

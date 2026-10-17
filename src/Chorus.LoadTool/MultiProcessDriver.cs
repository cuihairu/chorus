using System.Diagnostics;
using System.IO;

namespace Chorus.LoadTool;

/// <summary>
/// Coordinates independent load-tool processes. Each shard owns distinct
/// client ids and event ids while targeting its own fraction of the total rate;
/// together they remove the single driver process as the load ceiling.
/// </summary>
public static class MultiProcessDriver
{
    public static async Task<int> RunAsync(LoadToolOptions options, string[] originalArgs)
    {
        if (options.DriverProcesses <= 1)
        {
            throw new ArgumentException("Multi-process mode requires at least two processes.", nameof(options));
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var processes = new List<(Process Process, Task<int> Exit)>();
        try
        {
            for (var shard = 0; shard < options.DriverProcesses; shard++)
            {
                var process = StartShard(options, originalArgs, shard);
                processes.Add((process, MonitorExitAsync(process, cts.Token)));
                Console.WriteLine($"[loadtool] driver shard={shard + 1}/{options.DriverProcesses} pid={process.Id}");
            }

            await Task.WhenAll(processes.Select(item => item.Exit)).ConfigureAwait(false);
            var exits = await Task.WhenAll(processes.Select(item => item.Exit)).ConfigureAwait(false);
            return exits.Max();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return -1;
        }
        finally
        {
            foreach (var (process, _) in processes)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.Dispose();
            }
        }
    }

    public static double ShardRate(double totalRate, int shardIndex, int shardCount)
    {
        if (shardCount < 1 || shardIndex < 0 || shardIndex >= shardCount)
        {
            throw new ArgumentOutOfRangeException(nameof(shardIndex));
        }
        if (totalRate < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalRate));
        }

        return totalRate / shardCount;
    }

    public static string ShardNamespace(int shardIndex) => $"shard{shardIndex + 1}";

    public static string[] BuildShardArgs(string[] originalArgs, int shardIndex, int shardCount, double shardRate)
    {
        var ignored = new HashSet<string>(
        [
            "--driver-processes", "--driver-shard-index", "--driver-shard-count", "--driver-rate", "--event-namespace"
        ], StringComparer.Ordinal);
        var args = new List<string>();
        for (var index = 0; index < originalArgs.Length; index++)
        {
            if (ignored.Contains(originalArgs[index]))
            {
                index++;
                continue;
            }

            args.Add(originalArgs[index]);
        }

        args.AddRange([
            "--driver-shard-index", shardIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--driver-shard-count", shardCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--driver-rate", shardRate.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture),
            "--event-namespace", ShardNamespace(shardIndex)
        ]);
        return args.ToArray();
    }

    public static ProcessStartInfo BuildStartInfo(
        string hostExecutable, string? assemblyPath, string[] originalArgs, int shardIndex, int shardCount, double shardRate)
    {
        var startInfo = new ProcessStartInfo(hostExecutable);
        var shardArgs = BuildShardArgs(originalArgs, shardIndex, shardCount, shardRate);
        foreach (var argument in BuildProcessArgs(hostExecutable, assemblyPath, shardArgs))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.UseShellExecute = false;
        return startInfo;
    }

    private static Process StartShard(LoadToolOptions options, string[] originalArgs, int shardIndex)
    {
        var shardRate = ShardRate(options.EffectiveEventsPerSecondTotal, shardIndex, options.DriverProcesses);
        var startInfo = BuildStartInfo(
            ResolveHostExecutable(), ResolveAssemblyPath(), originalArgs, shardIndex, options.DriverProcesses, shardRate);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start driver shard.");
        return process;
    }

    private static async Task<int> MonitorExitAsync(Process process, CancellationToken cancellationToken)
    {
        while (!process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return process.HasExited ? process.ExitCode : -1;
    }

    public static string[] BuildProcessArgs(string executable, string? assemblyPath, string[] shardArgs)
    {
        var arguments = shardArgs;
        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return [executable, .. arguments];
        }

        if (Path.GetFileName(executable) is "dotnet" or "dotnet.exe")
        {
            arguments = arguments.Where(argument => argument != "--").ToArray();
            return string.IsNullOrWhiteSpace(assemblyPath) || assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) is false
                ? arguments
                : [assemblyPath, .. arguments];
        }

        return arguments;
    }

    private static string ResolveHostExecutable()
    {
        if (Environment.ProcessPath is { } processPath && File.Exists(processPath))
        {
            return processPath;
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }

    private static string? ResolveAssemblyPath()
    {
        var commandLine = Environment.GetCommandLineArgs().FirstOrDefault();
        return string.IsNullOrWhiteSpace(commandLine) ? null : commandLine;
    }
}

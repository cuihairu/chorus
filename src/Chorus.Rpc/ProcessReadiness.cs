using Grpc.Core;
using Grpc.Net.Client;

namespace Chorus.Rpc;

/// <summary>
/// The readiness vocabulary every process report speaks (FIX-LINK-01): whether the instance
/// can actually serve new work. A host reports its own true state through a dependency probe
/// rather than an assumed constant — an instance whose critical channel (the data plane for
/// game, streamer and platform-gateway hosts, the control plane for the streamer gateway) is
/// unreachable must never present itself as Ready, or the coordinator routes new rooms to a
/// host that cannot serve them. <c>Starting</c> means the probe has not passed yet in this
/// incarnation; <c>Unreachable</c> means it passed and was lost. The directory and the
/// scheduler treat any word other than <c>Ready</c> as not-servable, failing closed.
/// </summary>
public static class ProcessReadiness
{
    /// <summary>The instance's critical dependency answers; it may take new work.</summary>
    public const string Ready = "Ready";

    /// <summary>The dependency has not answered yet in this incarnation (still starting).</summary>
    public const string Starting = "Starting";

    /// <summary>The dependency answered and then went away — the instance lost what it serves on.</summary>
    public const string Unreachable = "Unreachable";

    /// <summary>Whether a readiness word means the instance may be offered new work. Fail
    /// closed: anything other than <c>Ready</c> — an unknown word included — never serves.</summary>
    public static bool Serves(string readiness)
        => string.Equals(readiness, Ready, StringComparison.Ordinal);
}

/// <summary>
/// The bounded dependency probes a self-reporting host runs each report tick (FIX-LINK-01):
/// one short <c>Ready</c> call on the channel the host actually serves on, so the reported
/// readiness word is the host's real state, never an assumption. Any failure — unreachable,
/// timeout, a cancelled tick — answers false; the reporter never crashes on a probe.
/// </summary>
public static class DependencyProbe
{
    /// <summary>A probe call is bounded so a dead dependency cannot stall a report cycle.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Probes the data plane over the host's own data channel (the game, streamer and
    /// platform-gateway hosts all serve rooms and sessions through it).</summary>
    public static async Task<bool> ProbeDataAsync(GrpcChannel channel, CancellationToken cancellationToken)
        => await ProbeAsync(new DataService.DataServiceClient(channel).ReadyAsync,
            cancellationToken);

    /// <summary>Probes the control plane over a control channel (the streamer gateway's routing
    /// source; without it the gateway cannot resolve the rooms it should fan a push to).</summary>
    public static async Task<bool> ProbeControlAsync(GrpcChannel channel, CancellationToken cancellationToken)
        => await ProbeAsync(new ControlService.ControlServiceClient(channel).ReadyAsync,
            cancellationToken);

    private static async Task<bool> ProbeAsync(
        Func<Empty, Metadata?, DateTime?, CancellationToken, AsyncUnaryCall<ReadyReply>> call,
        CancellationToken cancellationToken)
    {
        try
        {
            await call(new Empty(), null, DateTime.UtcNow.Add(CallTimeout), cancellationToken);
            return true;
        }
        catch (RpcException) { return false; }
        catch (OperationCanceledException) { return false; }
    }
}

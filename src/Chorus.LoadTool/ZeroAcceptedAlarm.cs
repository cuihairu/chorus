namespace Chorus.LoadTool;

/// <summary>
/// End-of-run alarm for a load run that sent events but had none accepted.
/// A real 2026-10-15 measurement run burned its whole duration on fail-closed
/// 401s (ok=0, other=6316) because the gateway had Ingress:Douyin:AppSecret
/// configured while the driver posted unsigned - the summary looked ordinary
/// and nothing was measured. The alarm fails loudly instead: a warning on
/// console error and a non-zero process exit code (no test or documented
/// usage depends on the load tool's exit code).
/// </summary>
public static class ZeroAcceptedAlarm
{
    public static bool ShouldAlarm(long eventsSent, long accepted)
        => eventsSent > 0 && accepted == 0;

    public static string Message(long eventsSent, long accepted)
        => $"[loadtool] ALARM: sent={eventsSent} accepted=0 - the gateway rejected every ingress event. " +
           "Platform signature verification is a likely cause: if the gateway has " +
           "Ingress:Douyin:AppSecret configured, sign events with --sign <secret> " +
           "(or CHORUS_LOAD_DOUYIN_SECRET). Connection failures (err>0) or ingress " +
           "rate limiting (429) produce the same zero-accepted shape.";

    /// <summary>Writes the alarm to stderr when zero events were accepted and
    /// returns the process exit code (1 on alarm, 0 otherwise).</summary>
    public static int Report(long eventsSent, long accepted)
    {
        if (!ShouldAlarm(eventsSent, accepted))
        {
            return 0;
        }

        Console.Error.WriteLine(Message(eventsSent, accepted));
        return 1;
    }
}

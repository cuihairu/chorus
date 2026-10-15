namespace Chorus.LoadTool;

/// <summary>
/// Run diagnostics for the ingress face: the loud signals a silent-failure run
/// used to lack. A real 2026-10-15 measurement burned its whole duration on
/// pure fail-closed 401s (ok=0, other=6316) with an ordinary-looking summary;
/// the startup hint and the end-of-run alarm make that mode visible, and the
/// non-zero exit code fails CI runs loudly. No test or documented usage
/// depends on the load tool's exit code.
/// </summary>
public static class IngressDiagnostics
{
    public const int Ok = 0;
    public const int NothingAccepted = 1;

    /// <summary>Process exit code: non-zero when the run sent events but the
    /// gateway accepted none.</summary>
    public static int ExitCodeFor(long sent, long accepted)
        => sent > 0 && accepted == 0 ? NothingAccepted : Ok;

    public static string NothingAcceptedWarning(string platform, long sent, long unauthorized)
        => $"[loadtool] ALARM: sent={sent} accepted=0 (401/unauthorized={unauthorized}) on platform={platform} - " +
           "the gateway rejected every ingress event. Platform signature verification is the usual cause: " +
           "if the gateway has Ingress:Douyin:AppSecret (or Ingress:Kuaishou:VerifyToken) configured, " +
           "sign events with --sign <secret> (or CHORUS_LOAD_SIGN_SECRET). " +
           "Connection errors and 429 rate limiting produce the same zero-accepted shape.";

    /// <summary>Startup hint for a run that will post unsigned.</summary>
    public static string UnsignedRunWarning(string platform)
        => IngressSigner.CanSign(platform)
            ? $"[loadtool] WARNING: platform={platform} ingress is fail-closed when the gateway has a secret " +
              "configured; this run posts UNSIGNED and will measure its own 401s unless you pass " +
              "--sign <secret> (or the gateway runs the unsigned demo verifier via Ingress:AllowAll)."
            : $"[loadtool] note: platform={platform} has no signature scheme in the load tool; unsigned posts " +
              "only work for platforms the gateway accepts without verification (e.g. Ingress:AllowAll test/demo).";
}

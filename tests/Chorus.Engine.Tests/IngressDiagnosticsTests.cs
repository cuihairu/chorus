using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// Zero-accepted alarm coverage: the 2026-10-15 run burned its whole duration
/// on pure 401s (ok=0, other=6316) with an ordinary-looking summary. Program.cs
/// prints the warning to stderr and returns the exit code; the decision
/// functions are tested here.
/// </summary>
public sealed class IngressDiagnosticsTests
{
    [Fact]
    public void ExitCode_ZeroAcceptedAfterSending_IsNonZero()
    {
        Assert.Equal(IngressDiagnostics.Ok, IngressDiagnostics.ExitCodeFor(sent: 0, accepted: 0));
        Assert.Equal(IngressDiagnostics.Ok, IngressDiagnostics.ExitCodeFor(sent: 100, accepted: 1));
        Assert.Equal(IngressDiagnostics.NothingAccepted, IngressDiagnostics.ExitCodeFor(sent: 6316, accepted: 0));
    }

    [Fact]
    public void NothingAcceptedWarning_NamesTheSignatureKnobs()
    {
        var warning = IngressDiagnostics.NothingAcceptedWarning("douyin", sent: 6316, unauthorized: 6316);

        Assert.Contains("accepted=0", warning);
        Assert.Contains("6316", warning);
        Assert.Contains("--sign", warning);
        Assert.Contains("CHORUS_LOAD_SIGN_SECRET", warning);
        Assert.Contains("Ingress:Douyin:AppSecret", warning);
        Assert.Contains("signature", warning);
    }

    [Theory]
    [InlineData("douyin", true)]
    [InlineData("kuaishou", true)]
    [InlineData("test", false)]
    public void UnsignedRunWarning_ExplainsWhatToDoPerPlatform(string platform, bool signable)
    {
        var warning = IngressDiagnostics.UnsignedRunWarning(platform);

        Assert.NotEqual(string.Empty, warning);
        if (signable)
        {
            Assert.Contains("--sign", warning);
            Assert.Contains("fail-closed", warning);
        }
        else
        {
            Assert.Contains(platform, warning);
        }
    }
}

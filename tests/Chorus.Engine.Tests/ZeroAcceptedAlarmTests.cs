using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// Zero-accepted alarm coverage: the 2026-10-15 run burned its whole duration
/// on pure 401s (ok=0, other=6316) with an ordinary-looking summary. The
/// decision function and message are tested; Report writes to stderr (the
/// production Program.cs wiring) and returns the exit code.
/// </summary>
public sealed class ZeroAcceptedAlarmTests
{
    [Fact]
    public void ShouldAlarm_WhenEventsSentButNoneAccepted()
    {
        Assert.True(ZeroAcceptedAlarm.ShouldAlarm(6316, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 5)]
    [InlineData(1, 1)]
    [InlineData(10, 3)]
    public void ShouldAlarm_NotWhenNothingSentOrSomethingAccepted(long sent, long accepted)
    {
        Assert.False(ZeroAcceptedAlarm.ShouldAlarm(sent, accepted));
    }

    [Fact]
    public void Message_PointsAtSignatureAndSignOption()
    {
        var message = ZeroAcceptedAlarm.Message(6316, 0);

        Assert.Contains("accepted=0", message);
        Assert.Contains("Ingress:Douyin:AppSecret", message);
        Assert.Contains("--sign", message);
        Assert.Contains("CHORUS_LOAD_DOUYIN_SECRET", message);
        Assert.Contains("signature", message);
    }

    [Fact]
    public void Report_ReturnsNonZeroExitCodeOnZeroAccepted()
    {
        Assert.Equal(1, ZeroAcceptedAlarm.Report(6316, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 3)]
    public void Report_ReturnsZeroExitCodeWhenHealthy(long sent, long accepted)
    {
        Assert.Equal(0, ZeroAcceptedAlarm.Report(sent, accepted));
    }
}

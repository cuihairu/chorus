using Chorus.EngineHost;

namespace Chorus.Engine.Tests;

/// <summary>
/// Cadence of the engine's amortized idle-sweep trigger (EngineIpcService
/// receive loop). The 2026-10-15 gate reproduction collapsed event delivery
/// to ~5% because the trigger fired a full sweep on EVERY message once the
/// session count reached the cap (800=800 at gate scale), drowning the single
/// receive loop in sweep work (each sweep probing every session against every
/// client key). At capacity the gate must tighten to one sweep per second —
/// enough to free dead client-less streams for new ones — and below capacity
/// it must stay at the 30s idle cadence.
/// </summary>
public sealed class SweepGateTests
{
    [Fact]
    public void FirstCall_SweepsImmediately()
    {
        var gate = new SweepGate(8);

        Assert.True(gate.ShouldSweep(1_000, 0));
    }

    [Fact]
    public void BelowCapacity_AtMostOncePerIdleInterval()
    {
        var gate = new SweepGate(8);

        Assert.True(gate.ShouldSweep(1_000, 7));
        Assert.False(gate.ShouldSweep(30_999, 7));
        Assert.True(gate.ShouldSweep(31_000, 7));
    }

    [Fact]
    public void AtCapacity_TightensToOneSecond()
    {
        var gate = new SweepGate(8);

        Assert.True(gate.ShouldSweep(1_000, 8));
        Assert.False(gate.ShouldSweep(1_999, 8));
        Assert.True(gate.ShouldSweep(2_000, 8));
    }

    [Fact]
    public void ZeroCap_Unlimited_NeverTightens()
    {
        // MaxSessions=0 means unlimited: no capacity exists to be at, so the
        // cadence must never tighten no matter how many sessions exist.
        var gate = new SweepGate(0);

        Assert.True(gate.ShouldSweep(1_000, 100));
        Assert.False(gate.ShouldSweep(2_000, 100));
        Assert.True(gate.ShouldSweep(31_000, 100));
    }

    [Fact]
    public void CountFallsBelowCap_IdleCadenceResumes()
    {
        var gate = new SweepGate(8);

        Assert.True(gate.ShouldSweep(1_000, 8));
        // The sweep freed sessions: the next trigger is 30s away again.
        Assert.False(gate.ShouldSweep(2_000, 7));
        Assert.True(gate.ShouldSweep(31_000, 7));
    }
}

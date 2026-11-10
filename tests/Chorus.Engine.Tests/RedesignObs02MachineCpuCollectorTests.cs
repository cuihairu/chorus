using Chorus.NodeAgent;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The machine CPU collector (FIX-OBS-02): the /proc/stat aggregate line parses into
/// (idle, total) tick counts, a utilization is a delta over an interval (the first
/// sample only establishes the baseline), idle+iowait counts as idle, guest ticks are
/// excluded, and a missing or malformed source yields no value instead of a crash —
/// a non-Linux node simply has no CPU sample.
/// </summary>
public sealed class RedesignObs02MachineCpuCollectorTests
{
    [Fact]
    public void ParsesTheAggregateLineIntoIdleAndTotal()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var parsed = MachineCpuCollector.Parse(
            "cpu 100 0 100 700 0 0 0 0 0 0");
        Assert.NotNull(parsed);
        Assert.Equal(700, parsed.Value.Idle);
        Assert.Equal(900, parsed.Value.Total);
    }

    [Fact]
    public void CountsIowaitAsIdle()
    {
        var parsed = MachineCpuCollector.Parse(
            "cpu 100 0 100 600 100 0 0 0");
        Assert.NotNull(parsed);
        // idle 600 + iowait 100 = 700 idle; total 900.
        Assert.Equal(700, parsed.Value.Idle);
        Assert.Equal(900, parsed.Value.Total);
    }

    [Fact]
    public void ExcludesGuestTicksWhichAreAlreadyInsideUserAndNice()
    {
        // The same busy/idle split with guest/guest_nice set must parse identically —
        // including them would double-count (they are already inside user/nice).
        var plain = MachineCpuCollector.Parse("cpu 100 0 100 700 0 0 0 0");
        var withGuest = MachineCpuCollector.Parse("cpu 100 0 100 700 0 0 0 0 50 0");
        Assert.Equal(plain, withGuest);
    }

    [Fact]
    public void ReturnsNullForMissingOrMalformedLines()
    {
        Assert.Null(MachineCpuCollector.Parse(null));
        Assert.Null(MachineCpuCollector.Parse(""));
        Assert.Null(MachineCpuCollector.Parse("cpu 100 0 100"));        // too few ticks
        Assert.Null(MachineCpuCollector.Parse("intr 1 2 3 4 5 6 7 8")); // wrong label
        Assert.Null(MachineCpuCollector.Parse("cpu not a number 1 2 3 4 5 6"));
    }

    [Fact]
    public void TheFirstSampleOnlyEstablishesTheBaseline()
    {
        var (collector, source) = CollectorOf("cpu 100 0 100 700 0 0 0 0");
        Assert.False(collector.TrySample(out _));

        source.Next = "cpu 150 0 150 950 0 0 0 0";
        Assert.True(collector.TrySample(out var percent));
        // total delta 350, idle delta 250 -> (1 - 250/350) * 100.
        Assert.Equal(28.5714285714, percent, 6);
    }

    [Fact]
    public void AThirdSampleAdvancesTheBaseline()
    {
        var (collector, source) = CollectorOf("cpu 100 0 100 700 0 0 0 0");
        Assert.False(collector.TrySample(out _));

        source.Next = "cpu 150 0 150 950 0 0 0 0";
        Assert.True(collector.TrySample(out _));

        // Baseline is now sample 2; sample 3 answers against it, not sample 1.
        source.Next = "cpu 200 0 200 1200 0 0 0 0";
        Assert.True(collector.TrySample(out var percent));
        // sample2->sample3: total 350, idle 250 -> 28.5714 (not the 43.75 a stale
        // sample-1 baseline would give).
        Assert.Equal(28.5714285714, percent, 6);
    }

    [Fact]
    public void NoValueWhenTheSourceDisappears()
    {
        var (collector, source) = CollectorOf("cpu 100 0 100 700 0 0 0 0");
        Assert.False(collector.TrySample(out _));

        // A non-Linux node has no /proc/stat: the read yields nothing and the collector
        // keeps answering "no value" instead of crashing or repeating a stale percent.
        source.Next = null;
        Assert.False(collector.TrySample(out _));
        source.Next = null;
        Assert.False(collector.TrySample(out _));
    }

    [Fact]
    public void NoValueWhenNoTicksElapsedBetweenSamples()
    {
        var (collector, source) = CollectorOf("cpu 100 0 100 700 0 0 0 0");
        Assert.False(collector.TrySample(out _));

        // The identical line again: zero elapsed ticks, no utilizable interval.
        source.Next = "cpu 100 0 100 700 0 0 0 0";
        Assert.False(collector.TrySample(out _));
    }

    private sealed class Source
    {
        public string? Next;
    }

    private static (MachineCpuCollector Collector, Source Source) CollectorOf(string first)
    {
        var source = new Source { Next = first };
        return (new MachineCpuCollector(() => source.Next), source);
    }
}

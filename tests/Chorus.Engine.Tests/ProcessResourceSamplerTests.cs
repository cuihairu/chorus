using Chorus.Control;

namespace Chorus.Engine.Tests;

public sealed class ProcessResourceSamplerTests
{
    [Fact]
    public void ReportsNormalizedCpuAndMemoryFromConsecutiveSamples()
    {
        var clock=new FakeClock();
        var cpu=TimeSpan.Zero;
        var memory=100L;
        var sampler=new ProcessResourceSampler(()=>cpu,()=>memory,clock,processorCount:2);

        var first=sampler.Sample();
        Assert.Equal(0,first.CpuPercent);
        Assert.Equal(100,first.MemoryBytes);
        clock.Advance(1000); cpu=TimeSpan.FromMilliseconds(500); memory=250;
        var second=sampler.Sample();
        Assert.Equal(25,second.CpuPercent);
        Assert.Equal(250,second.MemoryBytes);
    }

    [Fact]
    public void CpuReadingIsClampedToMachineCapacity()
    {
        var clock=new FakeClock(); var cpu=TimeSpan.Zero;
        var sampler=new ProcessResourceSampler(()=>cpu,()=>0,clock,processorCount:1);
        sampler.Sample(); clock.Advance(1000); cpu=TimeSpan.FromSeconds(3);
        Assert.Equal(100,sampler.Sample().CpuPercent);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now=DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow()=>_now;
        public void Advance(int ms)=>_now=_now.AddMilliseconds(ms);
    }
}

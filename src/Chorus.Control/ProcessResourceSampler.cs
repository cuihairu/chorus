namespace Chorus.Control;

public readonly record struct ProcessResourceUsage(double CpuPercent,long MemoryBytes);

/// <summary>Samples process CPU as a percentage of total machine CPU capacity.</summary>
public sealed class ProcessResourceSampler
{
    private readonly Func<TimeSpan> _cpuTime;
    private readonly Func<long> _memoryBytes;
    private readonly TimeProvider _clock;
    private readonly int _processorCount;
    private readonly object _gate=new();
    private DateTimeOffset? _previousAt;
    private TimeSpan _previousCpu;

    public ProcessResourceSampler(Func<TimeSpan> cpuTime,Func<long> memoryBytes,
        TimeProvider? clock=null,int? processorCount=null)
    {
        _cpuTime=cpuTime??throw new ArgumentNullException(nameof(cpuTime));
        _memoryBytes=memoryBytes??throw new ArgumentNullException(nameof(memoryBytes));
        _clock=clock??TimeProvider.System;
        _processorCount=Math.Max(1,processorCount??Environment.ProcessorCount);
    }

    public ProcessResourceUsage Sample()
    {
        lock(_gate)
        {
            var now=_clock.GetUtcNow();
            var cpu=_cpuTime();
            var memory=Math.Max(0,_memoryBytes());
            var percent=0d;
            if(_previousAt is { } previousAt)
            {
                var elapsed=(now-previousAt).TotalMilliseconds;
                if(elapsed>0) percent=Math.Clamp((cpu-_previousCpu).TotalMilliseconds/(elapsed*_processorCount)*100d,0d,100d);
            }
            _previousAt=now;
            _previousCpu=cpu;
            return new ProcessResourceUsage(percent,memory);
        }
    }
}

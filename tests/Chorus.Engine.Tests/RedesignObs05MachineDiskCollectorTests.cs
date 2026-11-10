using Chorus.NodeAgent;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The machine disk collector (FIX-OBS-05): df -Pk output parses into one sample per
/// usable mounted filesystem (total/used/percent bytes), pseudo and memory-backed
/// filesystems are not disk and are skipped, overlay (the container root) is kept, a
/// missing or malformed source yields no value, and an injected source drives the
/// collector.
/// </summary>
public sealed class RedesignObs05MachineDiskCollectorTests
{
    private const string DfOutput =
        """
        Filesystem     1024-blocks      Used Available Capacity Mounted on
        /dev/sda1         20511312  11234567   9276745      55% /
        tmpfs               655360         0    655360       0% /dev/shm
        /dev/sdb1          10240000   2048000   8192000      20% /data
        proc                     0         0         0        - /proc
        overlay           41943040  10485760  31457280      25% /app
        """;

    [Fact]
    public void ParsesBlockDevicesAndOverlayPerMount()
    {
        var samples = MachineDiskCollector.Parse(DfOutput);
        Assert.NotNull(samples);
        Assert.Equal(3, samples.Count);
        // /dev/sda1: 20511312*1024 total, 11234567*1024 used = 54.77%.
        Assert.Equal("/", samples[0].Mount);
        Assert.Equal(20511312L * 1024, samples[0].TotalBytes);
        Assert.Equal(11234567L * 1024, samples[0].UsedBytes);
        Assert.Equal(11234567.0 / 20511312.0 * 100.0, samples[0].UsedPercent, 6);
        // /dev/sdb1: 20%.
        Assert.Equal("/data", samples[1].Mount);
        Assert.Equal(10240000L * 1024, samples[1].TotalBytes);
        Assert.Equal(2048000L * 1024, samples[1].UsedBytes);
        Assert.Equal(20.0, samples[1].UsedPercent, 6);
        // overlay is the container root backed by real storage: kept.
        Assert.Equal("/app", samples[2].Mount);
        Assert.Equal(41943040L * 1024, samples[2].TotalBytes);
        Assert.Equal(25.0, samples[2].UsedPercent, 6);
    }

    [Fact]
    public void SkipsPseudoAndMemoryBackedFilesystems()
    {
        // tmpfs (memory-backed) and proc (pseudo) are not disk — never reported.
        var samples = MachineDiskCollector.Parse(DfOutput);
        Assert.NotNull(samples);
        Assert.DoesNotContain(samples, s => s.Mount == "/dev/shm");
        Assert.DoesNotContain(samples, s => s.Mount == "/proc");
    }

    [Fact]
    public void UnescapesMountPointsWithSpaces()
    {
        // A mount point with a space appears as \040 in df's POSIX output.
        var df = "Filesystem 1024-blocks Used Available Capacity Mounted on\n" +
                 "/dev/sda1 100000 25000 75000 25% /mnt/My\\040Data\n";
        var samples = MachineDiskCollector.Parse(df);
        Assert.NotNull(samples);
        Assert.Single(samples);
        Assert.Equal("/mnt/My Data", samples[0].Mount);
        Assert.Equal(25.0, samples[0].UsedPercent, 6);
    }

    [Fact]
    public void ReturnsNullForMissingMalformedOrAllPseudo()
    {
        Assert.Null(MachineDiskCollector.Parse(null));
        Assert.Null(MachineDiskCollector.Parse(""));
        // Header only: no usable mount.
        Assert.Null(MachineDiskCollector.Parse("Filesystem 1024-blocks Used Available Capacity Mounted on\n"));
        // Only pseudo filesystems: no disk.
        Assert.Null(MachineDiskCollector.Parse(
            "Filesystem 1024-blocks Used Available Capacity Mounted on\n" +
            "tmpfs 655360 0 655360 0% /dev/shm\n" +
            "proc 0 0 0 - /proc\n"));
        // A zero-total real device is unusable.
        Assert.Null(MachineDiskCollector.Parse(
            "Filesystem 1024-blocks Used Available Capacity Mounted on\n" +
            "/dev/sda1 0 0 0 - /\n"));
    }

    [Fact]
    public void TheCollectorAnswersFromTheInjectedSource()
    {
        string? content = DfOutput;
        var collector = new MachineDiskCollector(() => content);
        Assert.True(collector.TrySample(out var samples));
        Assert.Equal(3, samples.Count);
        Assert.Equal(20.0, samples[1].UsedPercent, 6);

        // A non-Linux node has no df: absent, never a fake zero.
        content = null;
        Assert.False(collector.TrySample(out _));
    }
}

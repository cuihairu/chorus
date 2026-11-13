using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

/// <summary>
/// B 站桥持久待投递 lanes 的文件后端（review C3）：保存/取出按最旧优先、成功后删除、
/// 目录不存在时创建。
/// </summary>
public sealed class BilibiliIngressSpoolTests
{
    [Fact]
    public void SaveTakeRemoveRoundTripsOldestFirst()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "chorus-bili-spool-unit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new BilibiliIngressSpool(directory);
            Assert.Equal(0, spool.Count());

            spool.Save("""{"n":1}""");
            spool.Save("""{"n":2}""");
            spool.Save("""{"n":3}""");
            Assert.Equal(3, spool.Count());

            var head = Assert.Single(spool.Take(1));
            Assert.Equal("""{"n":1}""", head.Json);

            spool.Remove(head.Path);
            Assert.Equal(2, spool.Count());

            var rest = spool.Take(10);
            Assert.Equal(2, rest.Count);
            Assert.Equal("""{"n":2}""", rest[0].Json);
            Assert.Equal("""{"n":3}""", rest[1].Json);

            // 删除后再取出即空：成功投递的载荷不再重发。
            spool.Remove(rest[0].Path);
            spool.Remove(rest[1].Path);
            Assert.Equal(0, spool.Count());
            Assert.Empty(spool.Take(10));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

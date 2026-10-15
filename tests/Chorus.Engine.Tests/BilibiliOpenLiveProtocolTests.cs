using System.IO.Compression;
using System.Text;
using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

/// <summary>
/// B站 open-live wss 帧编解码金测（blivedm ws_base.py 头格式 &gt;I2H2I，✅ 原文核实）：
/// 大端 16 字节头、一帧多包、ver=2 zlib（开放平台在用）解压后递归分包。
/// </summary>
public sealed class BilibiliOpenLiveProtocolTests
{
    [Fact]
    public void MakePacket_WritesBigEndianHeader()
    {
        var body = Encoding.UTF8.GetBytes("""{"code":0}""");

        var packet = BilibiliOpenLiveProtocol.MakePacket(body, BilibiliOpenLiveProtocol.OperationAuthReply);

        var (op, ver, parsedBody) = Assert.Single(BilibiliOpenLiveProtocol.ParseFrames(packet));
        Assert.Equal(BilibiliOpenLiveProtocol.OperationAuthReply, op);
        Assert.Equal(1, ver);
        Assert.Equal("""{"code":0}""", Encoding.UTF8.GetString(parsedBody));
        // pack_len 覆盖头+体。
        var packLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(0, 4));
        Assert.Equal(BilibiliOpenLiveProtocol.HeaderSize + body.Length, packLen);
    }

    [Fact]
    public void ParseFrames_SplitsMultiPacketFrame()
    {
        var first = BilibiliOpenLiveProtocol.MakePacket(
            Encoding.UTF8.GetBytes("""{"cmd":"a"}"""), BilibiliOpenLiveProtocol.OperationMessage);
        var second = BilibiliOpenLiveProtocol.MakePacket(
            Encoding.UTF8.GetBytes("""{"cmd":"b"}"""), BilibiliOpenLiveProtocol.OperationHeartbeatReply);

        var frames = BilibiliOpenLiveProtocol.ParseFrames(first.Concat(second).ToArray()).ToList();

        Assert.Equal(2, frames.Count);
        Assert.Equal("""{"cmd":"a"}""", Encoding.UTF8.GetString(frames[0].Body));
        Assert.Equal("""{"cmd":"b"}""", Encoding.UTF8.GetString(frames[1].Body));
    }

    [Fact]
    public void ParseFrames_TruncatedTail_StopsCleanly()
    {
        var packet = BilibiliOpenLiveProtocol.MakePacket(
            Encoding.UTF8.GetBytes("x"), BilibiliOpenLiveProtocol.OperationHeartbeat);

        var truncated = packet[..^3];
        // 头不完整（pack_len 越界）→ 静默停止，不抛。
        Assert.Empty(BilibiliOpenLiveProtocol.ParseFrames(truncated));
    }

    [Fact]
    public void ParseFrames_CorruptHeader_YieldsNothing()
    {
        // pack_len < HeaderSize (corrupt or truncated header) → yield break, no frames.
        var corrupt = new byte[BilibiliOpenLiveProtocol.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(corrupt.AsSpan(0, 4), 4);
        Assert.Empty(BilibiliOpenLiveProtocol.ParseFrames(corrupt));
    }

    [Fact]
    public void ParseFrames_PackLenExceedsData_YieldsNothing()
    {
        // pack_len > data.Length → yield break, no frames.
        var corrupt = new byte[BilibiliOpenLiveProtocol.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(corrupt.AsSpan(0, 4), 1024);
        Assert.Empty(BilibiliOpenLiveProtocol.ParseFrames(corrupt));
    }

    [Fact]
    public void DecodeBody_Deflate_RoundTrip()
    {
        var original = Encoding.UTF8.GetBytes("""{"cmd":"DANMU_MSG","data":{"msg":"压缩弹幕"}}""");
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(original);
        }

        var decoded = BilibiliOpenLiveProtocol.DecodeBody(
            BilibiliOpenLiveProtocol.ProtoVerDeflate, compressed.ToArray());

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeBody_Brotli_RoundTrip()
    {
        var original = Encoding.UTF8.GetBytes("""{"cmd":"LIVE_START"}""");
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(original);
        }

        var decoded = BilibiliOpenLiveProtocol.DecodeBody(
            BilibiliOpenLiveProtocol.ProtoVerBrotli, compressed.ToArray());

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeBody_Normal_ReturnsBody()
    {
        var body = Encoding.UTF8.GetBytes("""{"cmd":"NORMAL"}""");

        var decoded = BilibiliOpenLiveProtocol.DecodeBody(
            BilibiliOpenLiveProtocol.ProtoVerNormal, body);

        Assert.Equal(body, decoded);
    }

    [Fact]
    public void DecodeBody_UnknownVersion_Throws()
    {
        var body = Encoding.UTF8.GetBytes("x");

        var ex = Assert.Throws<InvalidOperationException>(
            () => BilibiliOpenLiveProtocol.DecodeBody(99, body));

        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecodeFrames_RecursesIntoDeflatedInnerPackets()
    {
        // 内层：两个明文业务包拼在一起，整体 zlib 压缩后作为外层 ver=2 的包体。
        var innerFirst = BilibiliOpenLiveProtocol.MakePacket(
            Encoding.UTF8.GetBytes("""{"cmd":"a"}"""), BilibiliOpenLiveProtocol.OperationMessage);
        var innerSecond = BilibiliOpenLiveProtocol.MakePacket(
            Encoding.UTF8.GetBytes("""{"cmd":"b"}"""), BilibiliOpenLiveProtocol.OperationMessage);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(innerFirst.Concat(innerSecond).ToArray());
        }

        var outer = BilibiliOpenLiveProtocol.MakePacket(
            compressed.ToArray(),
            BilibiliOpenLiveProtocol.OperationMessage,
            protoVer: BilibiliOpenLiveProtocol.ProtoVerDeflate);

        var decoded = BilibiliOpenLiveProtocol.DecodeFrames(outer).ToList();

        Assert.Equal(2, decoded.Count);
        Assert.Equal("""{"cmd":"a"}""", Encoding.UTF8.GetString(decoded[0].Body));
        Assert.Equal("""{"cmd":"b"}""", Encoding.UTF8.GetString(decoded[1].Body));
    }
}

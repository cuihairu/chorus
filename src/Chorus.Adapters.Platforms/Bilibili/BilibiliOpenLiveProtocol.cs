using System.Buffers.Binary;
using System.IO.Compression;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// B站 open-live wss 帧编解码（blivedm <c>clients/ws_base.py</c>，✅ 原文核实）：
/// 16 字节大包头 &gt;I2H2I（pack_len / raw_header_size=16 / ver / operation / seq_id=1），
/// 只处理二进制帧；业务帧 op=5（SEND_MSG_REPLY）可能一帧多包；
/// ver=0 明文 JSON、ver=2 zlib deflate（开放平台在用）、ver=3 brotli。
/// </summary>
public static class BilibiliOpenLiveProtocol
{
    public const int OperationHeartbeat = 2;
    public const int OperationHeartbeatReply = 3;
    public const int OperationMessage = 5;
    public const int OperationAuth = 7;
    public const int OperationAuthReply = 8;

    public const int ProtoVerNormal = 0;
    public const int ProtoVerDeflate = 2;
    public const int ProtoVerBrotli = 3;

    public const int HeaderSize = 16;

    public static byte[] MakePacket(ReadOnlySpan<byte> body, int operation, int protoVer = 1)
    {
        var packet = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), HeaderSize);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), (short)protoVer);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), operation);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), 1);
        body.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>把一段二进制帧拆成若干包（一帧多包）。</summary>
    public static IEnumerable<(int Operation, int Ver, byte[] Body)> ParseFrames(byte[] data)
    {
        var offset = 0;
        while (offset + HeaderSize <= data.Length)
        {
            var packLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            if (packLen < HeaderSize || offset + packLen > data.Length)
            {
                yield break;
            }

            var ver = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset + 6, 2));
            var op = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 8, 4));
            var body = new byte[packLen - HeaderSize];
            Array.Copy(data, offset + HeaderSize, body, 0, body.Length);
            yield return (op, ver, body);
            offset += packLen;
        }
    }

    /// <summary>解压包体（ver=2 zlib / ver=3 brotli；ver=0 原样）。</summary>
    public static byte[] DecodeBody(int ver, byte[] body)
    {
        switch (ver)
        {
            case ProtoVerNormal:
                return body;

            case ProtoVerDeflate:
                using (var input = new MemoryStream(body))
                using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    zlib.CopyTo(output);
                    return output.ToArray();
                }

            case ProtoVerBrotli:
                using (var input = new MemoryStream(body))
                using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    brotli.CopyTo(output);
                    return output.ToArray();
                }

            default:
                throw new InvalidOperationException($"unknown bilibili open-live proto ver {ver}");
        }
    }

    /// <summary>解压压缩包体后递归再分包（与 blivedm 的 _parse_ws_message 递归一致）。</summary>
    public static IEnumerable<(int Operation, int Ver, byte[] Body)> DecodeFrames(byte[] data)
    {
        foreach (var (op, ver, body) in ParseFrames(data))
        {
            if (ver == ProtoVerDeflate || ver == ProtoVerBrotli)
            {
                foreach (var inner in DecodeFrames(DecodeBody(ver, body)))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return (op, ver, body);
            }
        }
    }
}

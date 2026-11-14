using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Chorus.Messaging.Transports;

/// <summary>自管路径删除权的 AF_UNIX 端点。.NET 6 起 Socket.Dispose 会无条件
/// unlink 用 UnixDomainSocketEndPoint 绑定的路径（dotnet/runtime#52103，无
/// opt-out）：滚动重启时旧进程 dispose 会误删新进程刚重绑的同名文件，内核
/// socket 未死但路径没了，后续连接全部 ENOENT。自定义 EndPoint 类型不进入
/// 该删除跟踪（BCL 只识别 UnixDomainSocketEndPoint 本类型），路径删除权回归
/// 绑定方自己按 bind 身份守卫执行（本总线与 DevToolsService 各自的删除守卫）。
/// 序列化布局照抄 UnixDomainSocketEndPoint：SocketAddress = family + UTF-8 路径 + null 终止。</summary>
public sealed class RawUnixEndPoint : EndPoint
{
    // sun_path 在 sockaddr_un 里的偏移：Linux 为 sa_family(2 字节)，
    // Darwin 为 sa_len+sa_family(1+1 字节)，两者同为 2。
    private const int PathOffset = 2;

    private readonly string _path;
    private readonly byte[] _encodedPath;

    public RawUnixEndPoint(string path)
    {
        _path = path;
        _encodedPath = new byte[Encoding.UTF8.GetByteCount(path) + 1];
        Encoding.UTF8.GetBytes(path, 0, path.Length, _encodedPath, 0);
    }

    private RawUnixEndPoint(string path, byte[] encodedPath)
    {
        _path = path;
        _encodedPath = encodedPath;
    }

    public override AddressFamily AddressFamily => AddressFamily.Unix;

    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(AddressFamily.Unix, PathOffset + _encodedPath.Length);
        for (var i = 0; i < _encodedPath.Length; i++)
        {
            address[PathOffset + i] = _encodedPath[i];
        }
        return address;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        var size = socketAddress.Size;
        if (size <= PathOffset)
        {
            return new RawUnixEndPoint(string.Empty, Array.Empty<byte>());
        }

        var bytes = new byte[size - PathOffset];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = socketAddress[PathOffset + i];
        }
        var length = bytes.Length;
        while (length > 0 && bytes[length - 1] == 0)
        {
            length--;
        }
        return new RawUnixEndPoint(Encoding.UTF8.GetString(bytes, 0, length), bytes);
    }

    public override string ToString() => _path;
}

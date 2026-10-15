using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Chorus.Messaging.Transports;

public sealed class UnixDomainSocketMessageBus : IMessageBus
{
    private readonly string _socketPath;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly ConcurrentBag<Task> _acceptTasks = new();
    private readonly Channel<Envelope> _inbox = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    private Task? _serverTask;
    private Socket? _listenSocket;
    private Socket? _clientSocket;
    private NetworkStream? _clientStream;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    /// <summary>Bind 时刻路径上 socket 文件的身份。Unix 上 CreationTimeUtc
    /// 取自 st_ctime：bind 创建文件后元数据不再变化，它就是 bind 时刻；
    /// 纳秒分辨率下不同次 bind 不会取到同一值。刻意不用 P/Invoke stat 做
    /// dev/ino 比对——struct stat 大小随 ABI 变化，声明错了就是栈越界写。</summary>
    private DateTime _boundCreatedAtUtc;
    private bool _hasBoundIdentity;

    public UnixDomainSocketMessageBus(string socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            throw new ArgumentException("Socket path is required.", nameof(socketPath));
        }
        _socketPath = socketPath;
    }

    public void StartServer(int backlog = 32)
    {
        if (_serverTask != null)
        {
            return;
        }

        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); } catch { }
        }

        var dir = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listenSocket.Bind(new RawUnixEndPoint(_socketPath));
        _listenSocket.Listen(backlog);
        RecordSocketIdentity();

        _serverTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket? accepted = null;
                try
                {
                    accepted = await _listenSocket.AcceptAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                var t = Task.Run(async () =>
                {
                    using var socket = accepted;
                    await using var stream = new NetworkStream(socket, ownsSocket: false);
                    while (!_cts.IsCancellationRequested)
                    {
                        byte[]? frame;
                        try
                        {
                            frame = await LengthPrefixedStream.ReadFrameAsync(stream, _cts.Token);
                        }
                        catch
                        {
                            break;
                        }

                        if (frame == null)
                        {
                            break;
                        }

                        var env = JsonEnvelopeCodec.Deserialize(frame);
                        await _inbox.Writer.WriteAsync(env, _cts.Token);
                    }
                }, _cts.Token);

                _acceptTasks.Add(t);
            }
        }, _cts.Token);
    }

    private void RecordSocketIdentity()
    {
        try
        {
            _boundCreatedAtUtc = File.GetCreationTimeUtc(_socketPath);
            _hasBoundIdentity = true;
        }
        catch
        {
            _hasBoundIdentity = false;
        }
    }

    private void DeleteSocketPathIfStillOurs()
    {
        // 重启竞态守卫：本总线关闭时按路径删 socket 文件，而新进程可能已
        // 删-绑抢走同名路径（新 socket 的 bind 时刻必然不同）——无脑删会砍断
        // 新进程的 socket 文件入口。仅当路径上的文件仍是本进程 bind 时那份才删。
        if (!_hasBoundIdentity)
        {
            // 身份未知：保守不删。残留文件由下次 StartServer 的删-绑流程兜底，
            // 删错新进程的路径代价更大。
            return;
        }

        try
        {
            var now = File.GetCreationTimeUtc(_socketPath);
            if (now == _boundCreatedAtUtc)
            {
                File.Delete(_socketPath);
            }
        }
        catch
        {
        }
    }

    public async ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await _clientLock.WaitAsync(cancellationToken);
        try
        {
            if (_clientSocket == null || _clientStream == null || !_clientSocket.Connected)
            {
                _clientStream?.Dispose();
                _clientSocket?.Dispose();
                _clientSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await _clientSocket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken);
                _clientStream = new NetworkStream(_clientSocket, ownsSocket: false);
            }

            var bytes = JsonEnvelopeCodec.Serialize(envelope);
            await LengthPrefixedStream.WriteFrameAsync(_clientStream, bytes, cancellationToken);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    public async IAsyncEnumerable<Envelope> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _inbox.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_inbox.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cts.Cancel();
        _inbox.Writer.TryComplete();

        try { if (_serverTask != null) await _serverTask; } catch { }
        foreach (var t in _acceptTasks)
        {
            try { await t; } catch { }
        }

        try
        {
            _listenSocket?.Dispose();
            _listenSocket = null;
        }
        catch
        {
        }

        // 路径删除放在 DisposeAsync 而非 accept 循环尾部：Task.Run(...) 带上
        // 已取消的 token 时任务可能从未被调度，守卫会整体跳过；DisposeAsync
        // 必然执行，bind 身份守卫负责所有时序下的安全删除。
        DeleteSocketPathIfStillOurs();

        await _clientLock.WaitAsync();
        try
        {
            _clientStream?.Dispose();
            _clientStream = null;
            _clientSocket?.Dispose();
            _clientSocket = null;
        }
        finally
        {
            _clientLock.Release();
            _clientLock.Dispose();
        }

        _cts.Dispose();
    }

    /// <summary>自管生命周期的 AF_UNIX 端点。.NET 6 起 Socket.Dispose 会无条件
    /// unlink 用 UnixDomainSocketEndPoint 绑定的路径（dotnet/runtime#52103，无
    /// opt-out）：滚动重启时旧进程 dispose 会误删新进程刚重绑的同名文件，内核
    /// socket 未死但路径没了，后续连接全部 ENOENT。自定义 EndPoint 类型不进入
    /// 该删除跟踪（BCL 只识别 UnixDomainSocketEndPoint 本类型），路径删除权回归
    /// 本总线，由 DeleteSocketPathIfStillOurs 按 bind 身份执行。序列化布局照抄
    /// UnixDomainSocketEndPoint：SocketAddress = family + UTF-8 路径 + null 终止。</summary>
    private sealed class RawUnixEndPoint : EndPoint
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
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Douyu ingress bridge SKELETON, 结构对齐 <see cref="BilibiliIngressBridgeHostedService"/>：
/// 官方事件通道形态（HTTP 回调 / 服务端长连）与鉴权物料都未取得，桥<b>不连接、不订阅、不转发</b>。
/// StartAsync 打出待料提示后原样返回（fail-visible），StopAsync 无资源可释放。
/// 不注册进任何宿主；官方协议落地后按 bilibili 桥的 client/protocol/signer 分层补齐。
/// 骨架预留 ≠ 支持；README/平台矩阵按 skeleton 级登记。
/// </summary>
public sealed class DouyuIngressBridgeSkeleton : IHostedService
{
    private readonly ILogger<DouyuIngressBridgeSkeleton>? _logger;
    private int _skeletonNoticeLogged;

    public DouyuIngressBridgeSkeleton(ILogger<DouyuIngressBridgeSkeleton>? logger = null) => _logger = logger;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _skeletonNoticeLogged, 1) == 0)
        {
            _logger?.LogWarning(
                "douyu ingress bridge is a skeleton: the official event channel and credentials are " +
                "unavailable, nothing is connected or forwarded. Register it only when the official " +
                "protocol materials exist.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

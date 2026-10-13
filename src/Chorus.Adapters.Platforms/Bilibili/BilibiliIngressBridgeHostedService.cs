using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Hosts <see cref="BilibiliOpenLiveClient"/> inside a chorus host (Server or
/// Gateway) when the full credential set is configured. Credentials come from
/// configuration (Ingress:Bilibili:*, 主播本人授权) and never enter the repo or
/// logs. With any required piece missing the bridge stays off (fail-visible:
/// /ingress/bilibili keeps answering fail-closed; start the bridge by
/// completing the config).
/// </summary>
public sealed class BilibiliIngressBridgeHostedService : IHostedService
{
    private readonly BilibiliOpenLiveClient _client;
    private readonly ILogger<BilibiliIngressBridgeHostedService> _logger;

    public BilibiliIngressBridgeHostedService(
        BilibiliOpenLiveClient client,
        ILogger<BilibiliIngressBridgeHostedService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "bilibili open-live bridge starting: events will be posted signed to {IngressUrl}",
            "Ingress:Bilibili:IngressUrl (configured)"); // 不打 URL 本身以外任何凭据
        _client.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync().ConfigureAwait(false);
        _logger.LogInformation(
            "bilibili open-live bridge stopped: appStartCalls={AppStartCalls} reconnects={Reconnects} eventsPosted={EventsPosted} postFailures={PostFailures}",
            Volatile.Read(ref _client.AppStartCalls),
            Volatile.Read(ref _client.Reconnects),
            Volatile.Read(ref _client.EventsPosted),
            Volatile.Read(ref _client.PostFailures));
    }

    /// <summary>凭据四件套 + 会话绑定 + 转发目标齐全才启动桥。</summary>
    public static bool IsConfigured(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration["Ingress:Bilibili:AccessKeyId"])
            && !string.IsNullOrWhiteSpace(configuration["Ingress:Bilibili:AccessKeySecret"])
            && !string.IsNullOrWhiteSpace(configuration["Ingress:Bilibili:RoomOwnerAuthCode"])
            && !string.IsNullOrWhiteSpace(configuration["Ingress:Bilibili:SessionId"])
            && !string.IsNullOrWhiteSpace(configuration["Ingress:Bilibili:IngressUrl"])
            && long.TryParse(configuration["Ingress:Bilibili:AppId"], out _);

    public static BilibiliIngressBridgeHostedService Create(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var options = new BilibiliOpenLiveOptions(
            AccessKeyId: configuration["Ingress:Bilibili:AccessKeyId"]!,
            AccessKeySecret: configuration["Ingress:Bilibili:AccessKeySecret"]!,
            AppId: long.Parse(configuration["Ingress:Bilibili:AppId"]!, System.Globalization.CultureInfo.InvariantCulture),
            RoomOwnerAuthCode: configuration["Ingress:Bilibili:RoomOwnerAuthCode"]!,
            IngressUrl: configuration["Ingress:Bilibili:IngressUrl"]!);
        var client = new BilibiliOpenLiveClient(
            options,
            new HttpMessageInvoker(new HttpClientHandler()),
            logger: serviceProvider.GetService<ILogger<BilibiliOpenLiveClient>>());
        return new BilibiliIngressBridgeHostedService(
            client,
            serviceProvider.GetRequiredService<ILogger<BilibiliIngressBridgeHostedService>>());
    }
}

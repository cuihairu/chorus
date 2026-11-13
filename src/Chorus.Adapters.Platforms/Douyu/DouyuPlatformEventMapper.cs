using Chorus.Adapters.Abstractions;
using Chorus.Protocol.Models;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Douyu event mapper SKELETON: 斗鱼事件 schema、事件 ID 唯一域、房间/用户标识与
/// 生命周期语义均无官方物料（docs/research/platforms/douyu-huya-bilibili-2026-10-30.md
/// 全列「待确认」），<b>任何字段映射都是编造</b>。骨架一律拒绝映射（返回 null）——
/// 不猜字段、不把互联网弹幕监听库当正式 SDK。官方 contract 到位后按
/// BilibiliPlatformEventMapper 的金测样式实现，本文件只占位命名空间与注册面。
/// </summary>
public sealed class DouyuPlatformEventMapper : IPlatformEventMapper
{
    public string PlatformName => PlatformNames.Douyu;

    public AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs)
    {
        // 骨架纪律：没有官方 schema 就没有映射。返回 null 是「无法映射」，
        // 不是「事件无效」——校验面在官方协议落地前对 douyu 保持 fail closed。
        return null;
    }
}

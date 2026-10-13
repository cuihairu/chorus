using Chorus.Protocol.Models;

namespace Chorus.Engine.Routing;

/// <summary>What a platform integration may do, beyond which event kinds it emits.</summary>
[Flags]
public enum PlatformCapabilityFlags
{
    /// <summary>Only ingest is possible (the default; B 站 P3 反向写入明确不做).</summary>
    None = 0,

    /// <summary>平台事件可入 chorus（只读输入侧）。</summary>
    Ingest = 1,

    /// <summary>chorus 可反向写入平台（当前无平台开放此通道）。</summary>
    Writeback = 2,
}

/// <summary>Declared capabilities of one platform integration.</summary>
/// <param name="SupportedEventKinds">Event kinds this platform's mapper actually produces (see AudienceEventKind).</param>
/// <param name="Flags">Direction flags; <see cref="PlatformCapabilityFlags.None"/> = ingest-only declaration.</param>
public sealed record PlatformCapabilities(
    IReadOnlySet<AudienceEventKind> SupportedEventKinds,
    PlatformCapabilityFlags Flags = PlatformCapabilityFlags.Ingest)
{
    /// <summary>
    /// B 站 open-live 已落地能力（BilibiliPlatformEventMapper）：弹幕/礼物/进场/房间生命周期；
    /// 只读输入，反向写入无官方通道（P3 明确不做）。
    /// </summary>
    public static PlatformCapabilities Bilibili { get; } = new(
        new HashSet<AudienceEventKind>
        {
            AudienceEventKind.Comment, AudienceEventKind.Gift, AudienceEventKind.Enter, AudienceEventKind.RoomLifecycle,
        });

    /// <summary>
    /// 抖音占位契约能力（DouyinPlatformEventMapper 占位 schema，非官方）：评论/礼物/点赞。
    /// </summary>
    public static PlatformCapabilities Douyin { get; } = new(
        new HashSet<AudienceEventKind>
        {
            AudienceEventKind.Comment, AudienceEventKind.Gift, AudienceEventKind.Like,
        });

    /// <summary>
    /// 快手占位契约能力（KuaishouPlatformEventMapper 占位 schema）：评论/礼物/点赞。
    /// </summary>
    public static PlatformCapabilities Kuaishou { get; } = new(
        new HashSet<AudienceEventKind>
        {
            AudienceEventKind.Comment, AudienceEventKind.Gift, AudienceEventKind.Like,
        });
}

/// <summary>
/// Platform capability registry (todo §14 control-plane item, contract = 总体架构审计 §17.3
/// 「平台能力注册」): the control-plane answer to "what can this platform integration do".
/// Lookup is fail-closed — an unregistered platform supports nothing until something
/// registers it (production seeding lands with the control-plane wiring; until then
/// Program.cs seeds the known-platform defaults). Platform names compare
/// case-insensitively, same as <see cref="PlatformBindingRegistry"/>.
/// </summary>
public sealed class PlatformCapabilityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PlatformCapabilities> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _capabilities.Count;
            }
        }
    }

    /// <summary>Declare (or replace) a platform's capabilities; idempotent whole-row replace.</summary>
    public void Register(string platform, PlatformCapabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentNullException.ThrowIfNull(capabilities);

        lock (_gate)
        {
            _capabilities[platform] = capabilities;
        }
    }

    /// <summary>The declared capabilities, or null when the platform is not registered.</summary>
    public bool TryGet(string platform, out PlatformCapabilities? capabilities)
    {
        lock (_gate)
        {
            return _capabilities.TryGetValue(platform, out capabilities!);
        }
    }

    /// <summary>Whether this platform's mapper may produce the kind; false for unregistered platforms.</summary>
    public bool SupportsEventKind(string platform, AudienceEventKind kind)
    {
        lock (_gate)
        {
            return _capabilities.TryGetValue(platform, out var caps) && caps.SupportedEventKinds.Contains(kind);
        }
    }

    /// <summary>Whether chorus may write back to the platform (never true today).</summary>
    public bool SupportsWriteback(string platform)
    {
        lock (_gate)
        {
            return _capabilities.TryGetValue(platform, out var caps)
                && (caps.Flags & PlatformCapabilityFlags.Writeback) != 0;
        }
    }

    /// <summary>A consistent view of all registered capabilities, for diagnostics.</summary>
    public IReadOnlyDictionary<string, PlatformCapabilities> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, PlatformCapabilities>(_capabilities, StringComparer.OrdinalIgnoreCase);
        }
    }
}

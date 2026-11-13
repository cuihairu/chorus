namespace Chorus.Adapters.Platforms;

public static class PlatformNames
{
    public const string Test = "test";
    public const string Simulator = "simulator";
    public const string Douyin = "douyin";
    public const string Kuaishou = "kuaishou";
    public const string Bilibili = "bilibili";
    // 骨架占位：douyu 骨架预留（官方协议物料未取得），huya 仅研究（旧开放 API 2026-10-31 停服，
    // 替代路线未见）。常量本身不注册任何实现——支持状态以 README/平台矩阵登记为准。
    public const string Douyu = "douyu";
    public const string Huya = "huya";
}

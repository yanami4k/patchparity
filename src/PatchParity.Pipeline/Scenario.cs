using System.Globalization;

namespace PatchParity.Pipeline;

/// <summary>
/// 验证场景。参数以字符串形式转发给热更侧的 harness，母包不解释其内容——
/// 母包发布后不可更新，把场景参数写进调用签名会使每加一个参数都要重打母包。
/// </summary>
public sealed record Scenario
{
    public int Seed { get; init; } = 42;
    public int Ticks { get; init; } = 600;
    public int Players { get; init; } = 2;

    /// <summary>
    /// 机器人数量。默认 0：启用机器人需要额外构造行为树系统并注册行为树根，
    /// 否则世界初始化会失败。
    /// </summary>
    public int Bots { get; init; }

    public int MaxEntities { get; init; } = 512;
    public int DeltaTimeMs { get; init; } = 25;

    /// <summary>用于缓存键与报告的稳定名字。</summary>
    public string Name => $"seed{Seed}-t{Ticks}-p{Players}-b{Bots}";

    public string ToOptionString()
    {
        var parts = new[]
        {
            "seed=" + Seed.ToString(CultureInfo.InvariantCulture),
            "ticks=" + Ticks.ToString(CultureInfo.InvariantCulture),
            "players=" + Players.ToString(CultureInfo.InvariantCulture),
            "bots=" + Bots.ToString(CultureInfo.InvariantCulture),
            "maxEntities=" + MaxEntities.ToString(CultureInfo.InvariantCulture),
            "deltaTimeMs=" + DeltaTimeMs.ToString(CultureInfo.InvariantCulture),
        };
        return string.Join(";", parts);
    }
}

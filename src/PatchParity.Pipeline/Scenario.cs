using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PatchParity.Pipeline;

/// <summary>
/// 验证场景。参数以字符串形式转发给热更侧的 harness，母包不解释其内容——
/// 母包发布后不可更新，把场景参数写进调用签名会使每加一个参数都要重打母包。
///
/// 场景生成用的参数（地面、移动速度、职业、出生位置、按键频率、追逐）只在给定时出现在选项串里，
/// 缺省场景的选项串与加入这些参数之前完全相同（裁决 019）。
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

    /// <summary>放一块覆盖整个场地的地面。缺省没有地面，角色一直下落。</summary>
    public bool Ground { get; init; }

    /// <summary>各职业的移动速度。缺省沿用 harness 的数据资产，速度为 0，角色不会移动。</summary>
    public decimal? MoveSpeed { get; init; }

    /// <summary>每名玩家的职业 0..3。缺省按种子取 0 或 1。</summary>
    public IReadOnlyList<int>? Classes { get; init; }

    /// <summary>每名玩家的出生位置 (x, z)。缺省按种子在 [-8, 8] 内取。</summary>
    public IReadOnlyList<(decimal X, decimal Z)>? Spawn { get; init; }

    /// <summary>每帧攻击的千分概率。缺省为 harness 原先的 1/10。</summary>
    public int? Attack { get; init; }

    /// <summary>每帧跳跃的千分概率。缺省为 harness 原先的 1/20。</summary>
    public int? Jump { get; init; }

    /// <summary>每帧放技能的千分概率。缺省不放。</summary>
    public int Skill { get; init; }

    /// <summary>朝最近的另一名角色移动。</summary>
    public bool Chase { get; init; }

    public bool IsCustomized =>
        Ground || MoveSpeed is not null || Classes is not null || Spawn is not null ||
        Attack is not null || Jump is not null || Skill != 0 || Chase;

    /// <summary>用于报告的稳定名字；带场景生成参数时附上选项串的摘要，同一组参数名字相同。</summary>
    public string Name
    {
        get
        {
            string name = $"seed{Seed}-t{Ticks}-p{Players}-b{Bots}";
            if (!IsCustomized)
            {
                return name;
            }
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToOptionString())))[..8].ToLowerInvariant();
            return name + "-" + digest;
        }
    }

    public string ToOptionString()
    {
        var parts = new List<string>
        {
            "seed=" + Seed.ToString(CultureInfo.InvariantCulture),
            "ticks=" + Ticks.ToString(CultureInfo.InvariantCulture),
            "players=" + Players.ToString(CultureInfo.InvariantCulture),
            "bots=" + Bots.ToString(CultureInfo.InvariantCulture),
            "maxEntities=" + MaxEntities.ToString(CultureInfo.InvariantCulture),
            "deltaTimeMs=" + DeltaTimeMs.ToString(CultureInfo.InvariantCulture),
        };
        if (Ground) parts.Add("ground=1");
        if (MoveSpeed is { } speed) parts.Add("moveSpeed=" + Number(speed));
        if (Classes is { } classes) parts.Add("classes=" + string.Join(",", classes.Select(c => c.ToString(CultureInfo.InvariantCulture))));
        if (Spawn is { } spawn) parts.Add("spawn=" + string.Join(",", spawn.Select(p => Number(p.X) + ":" + Number(p.Z))));
        if (Attack is { } attack) parts.Add("attack=" + attack.ToString(CultureInfo.InvariantCulture));
        if (Jump is { } jump) parts.Add("jump=" + jump.ToString(CultureInfo.InvariantCulture));
        if (Skill != 0) parts.Add("skill=" + Skill.ToString(CultureInfo.InvariantCulture));
        if (Chase) parts.Add("chase=1");
        return string.Join(";", parts);
    }

    /// <summary>可由调用方给定的参数及其取值说明，供工具说明与报错引用。</summary>
    public static readonly IReadOnlyDictionary<string, string> CustomParameters = new Dictionary<string, string>
    {
        ["seed"] = "整数，世界与输入的随机种子，缺省 42",
        ["ticks"] = "1..6000，模拟帧数（每帧 25 毫秒），缺省 600",
        ["players"] = "1..4，玩家数，缺省 2",
        ["ground"] = "0 或 1，放一块覆盖整个 40×40 场地的地面；缺省没有地面，角色一直下落、反复死亡复活",
        ["moveSpeed"] = "0..20，各职业的移动速度，至多两位小数；缺省 0，角色不会移动",
        ["classes"] = "逗号分隔，每名玩家一个职业 0..3（0 战士 1 法师 2 盗贼 3 骑士），个数须等于 players",
        ["spawn"] = "逗号分隔的 x:z，每名玩家一个出生位置，坐标在 -18..18 内、至多一位小数，个数须等于 players",
        ["attack"] = "0..1000，每帧攻击的千分概率；缺省约 100",
        ["jump"] = "0..1000，每帧跳跃的千分概率（须站在地面上才跳得起来）；缺省约 50",
        ["skill"] = "0..1000，每帧放技能的千分概率；缺省 0",
        ["chase"] = "0 或 1，朝最近的另一名角色移动（须 moveSpeed 大于 0 才动得了）",
    };

    /// <summary>
    /// 解析调用方给的 k=v;k=v 场景。只接受 <see cref="CustomParameters"/> 里的参数并校验范围：
    /// 场景是验证的输入，越界的值可能让 harness 静默退回缺省行为，得出的结论就不再对应调用方以为的场景。
    /// </summary>
    public static Scenario Parse(string options)
    {
        var scenario = new Scenario();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string pair in options.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                throw new FormatException($"场景参数须为 k=v：{pair}");
            }
            string key = pair[..eq].Trim();
            string value = pair[(eq + 1)..].Trim();
            if (!CustomParameters.ContainsKey(key))
            {
                throw new FormatException($"未知的场景参数 {key}，可用：{string.Join("、", CustomParameters.Keys)}");
            }
            if (!seen.Add(key))
            {
                throw new FormatException($"场景参数 {key} 重复");
            }
            raw[key] = value;
        }

        if (raw.TryGetValue("seed", out string? seed)) scenario = scenario with { Seed = Int(seed, "seed", int.MinValue, int.MaxValue) };
        if (raw.TryGetValue("ticks", out string? ticks)) scenario = scenario with { Ticks = Int(ticks, "ticks", 1, 6000) };
        if (raw.TryGetValue("players", out string? players)) scenario = scenario with { Players = Int(players, "players", 1, 4) };
        if (raw.TryGetValue("ground", out string? ground)) scenario = scenario with { Ground = Int(ground, "ground", 0, 1) == 1 };
        if (raw.TryGetValue("moveSpeed", out string? speed)) scenario = scenario with { MoveSpeed = Dec(speed, "moveSpeed", 0, 20, 2) };
        if (raw.TryGetValue("attack", out string? attack)) scenario = scenario with { Attack = Int(attack, "attack", 0, 1000) };
        if (raw.TryGetValue("jump", out string? jump)) scenario = scenario with { Jump = Int(jump, "jump", 0, 1000) };
        if (raw.TryGetValue("skill", out string? skill)) scenario = scenario with { Skill = Int(skill, "skill", 0, 1000) };
        if (raw.TryGetValue("chase", out string? chase)) scenario = scenario with { Chase = Int(chase, "chase", 0, 1) == 1 };

        if (raw.TryGetValue("classes", out string? classes))
        {
            int[] values = classes.Split(',', StringSplitOptions.TrimEntries).Select(c => Int(c, "classes", 0, 3)).ToArray();
            if (values.Length != scenario.Players)
            {
                throw new FormatException($"classes 给了 {values.Length} 个，须等于 players={scenario.Players}");
            }
            scenario = scenario with { Classes = values };
        }
        if (raw.TryGetValue("spawn", out string? spawn))
        {
            var points = spawn.Split(',', StringSplitOptions.TrimEntries).Select(p =>
            {
                string[] xz = p.Split(':', StringSplitOptions.TrimEntries);
                if (xz.Length != 2)
                {
                    throw new FormatException($"spawn 的每一项须为 x:z：{p}");
                }
                return (Dec(xz[0], "spawn", -18, 18, 1), Dec(xz[1], "spawn", -18, 18, 1));
            }).ToArray();
            if (points.Length != scenario.Players)
            {
                throw new FormatException($"spawn 给了 {points.Length} 个，须等于 players={scenario.Players}");
            }
            scenario = scenario with { Spawn = points };
        }
        return scenario;
    }

    private static int Int(string value, string key, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
        {
            throw new FormatException($"{key} 须为 {min}..{max} 的整数：{value}");
        }
        return n;
    }

    private static decimal Dec(string value, string key, decimal min, decimal max, int decimals)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal n) ||
            n < min || n > max || decimal.Round(n, decimals) != n)
        {
            throw new FormatException($"{key} 须在 {Number(min)}..{Number(max)} 内、至多 {decimals} 位小数：{value}");
        }
        return n;
    }

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

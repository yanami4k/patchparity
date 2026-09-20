namespace PatchParity.Pipeline;

/// <summary>
/// 判定前的两项自检。任一不通过，比对结论一律作废，不得报出"一致"。
///
/// 两项都是实测代价换来的：验证场景曾是空世界，改动点从未被执行，两路自然"一致"；
/// 修好世界初始化后又发现世界随机种子取自墙上时钟，同一场景两次运行本就不同。
/// 空世界还会掩盖非确定性，故两项互相不可替代。
/// </summary>
public static class Preconditions
{
    /// <summary>
    /// 确定性自检：同一场景的两次独立运行必须逐 tick 完全一致。
    /// 传入的两份序列必须来自同一构建、同一场景参数。
    /// </summary>
    public static DeterminismCheck CheckDeterminism(HashSeries first, HashSeries second)
    {
        ComparisonResult result = SeriesComparer.Compare(first, second);
        return new DeterminismCheck
        {
            Passed = result.Identical,
            FirstDivergentTick = result.FirstDivergentTick,
            DivergentLayers = result.DivergentLayers,
        };
    }

    /// <summary>
    /// 覆盖自检：世界非空，且改动声称触及的组件在场景中确实存在。
    /// touchedComponents 为空时只检查世界非空——这是最低限度，不足以证明改动点被执行，
    /// 故报告中须如实标注覆盖强度。
    /// </summary>
    public static CoverageCheck CheckCoverage(HashSeries series, IReadOnlyCollection<string> touchedComponents)
    {
        if (series.Count == 0)
        {
            return new CoverageCheck { Passed = false, LiveComponents = 0, Reason = "运行没有产出任何帧" };
        }

        int live = series.LiveComponents(series.Count - 1);
        if (live == 0)
        {
            return new CoverageCheck
            {
                Passed = false,
                LiveComponents = 0,
                Reason = "末帧组件实例总数为 0，世界是空的；此时两路一致不代表补丁等价",
            };
        }

        var missing = new List<string>();
        foreach (string component in touchedComponents)
        {
            bool present = false;
            for (int row = 0; row < series.Count; row++)
            {
                if (series.ComponentCount(row, component) > 0)
                {
                    present = true;
                    break;
                }
            }
            if (!present)
            {
                missing.Add(component);
            }
        }

        return new CoverageCheck
        {
            Passed = missing.Count == 0,
            LiveComponents = live,
            MissingComponents = missing,
            Strength = touchedComponents.Count == 0 ? CoverageStrength.WorldNotEmpty : CoverageStrength.TouchedComponentsPresent,
            Reason = missing.Count == 0
                ? null
                : "改动触及的组件在整个场景中从未出现: " + string.Join(", ", missing),
        };
    }
}

public sealed record DeterminismCheck
{
    public required bool Passed { get; init; }
    public int? FirstDivergentTick { get; init; }
    public IReadOnlyList<LayerDifference> DivergentLayers { get; init; } = Array.Empty<LayerDifference>();
}

/// <summary>覆盖证据的强度，须随判定一起报出，不得只说"通过"。</summary>
public enum CoverageStrength
{
    /// <summary>只知道世界非空，不知道改动点是否被执行。</summary>
    WorldNotEmpty,

    /// <summary>改动触及的组件在场景中出现过。</summary>
    TouchedComponentsPresent,
}

public sealed record CoverageCheck
{
    public required bool Passed { get; init; }
    public required int LiveComponents { get; init; }
    public CoverageStrength Strength { get; init; } = CoverageStrength.WorldNotEmpty;
    public IReadOnlyList<string> MissingComponents { get; init; } = Array.Empty<string>();
    public string? Reason { get; init; }
}

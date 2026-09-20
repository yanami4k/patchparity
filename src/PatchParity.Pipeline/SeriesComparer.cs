namespace PatchParity.Pipeline;

/// <summary>一处分层差异：层名与两侧取值。</summary>
public sealed record LayerDifference(string Layer, string Left, string Right);

/// <summary>比对结论。</summary>
public sealed record ComparisonResult
{
    /// <summary>逐 tick 总哈希是否全程一致。</summary>
    public required bool Identical { get; init; }

    /// <summary>首个总哈希不同的 tick；一致时为 null。</summary>
    public int? FirstDivergentTick { get; init; }

    /// <summary>该 tick 上取值不同的分层，按表头顺序。</summary>
    public IReadOnlyList<LayerDifference> DivergentLayers { get; init; } = Array.Empty<LayerDifference>();

    /// <summary>只在一侧出现的列名，两侧构建注册的组件集合不同时非空。</summary>
    public IReadOnlyList<string> LeftOnlyColumns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RightOnlyColumns { get; init; } = Array.Empty<string>();

    /// <summary>两侧帧数；不等本身即为一种差异。</summary>
    public required int LeftCount { get; init; }

    public required int RightCount { get; init; }

    /// <summary>
    /// 总哈希不同但所有共有分层都相同。说明差异落在分层未覆盖的部分，
    /// 定位信息不足以指向组件或系统，需要按原始状态进一步排查。
    /// </summary>
    public bool DivergedOutsideLayers => FirstDivergentTick is not null && DivergentLayers.Count == 0;
}

public static class SeriesComparer
{
    /// <summary>
    /// 逐 tick 比对两次运行。判定只看总哈希；一旦不同，就在该帧展开全部共有分层，
    /// 报出取值不同的层，用于把分歧定位到具体组件类型或系统。
    /// </summary>
    public static ComparisonResult Compare(HashSeries left, HashSeries right)
    {
        var shared = new List<string>();
        var leftOnly = new List<string>();
        foreach (string column in left.Columns)
        {
            if (right.HasColumn(column))
            {
                shared.Add(column);
            }
            else
            {
                leftOnly.Add(column);
            }
        }

        var rightOnly = new List<string>();
        foreach (string column in right.Columns)
        {
            if (!left.HasColumn(column))
            {
                rightOnly.Add(column);
            }
        }

        int common = Math.Min(left.Count, right.Count);
        for (int row = 0; row < common; row++)
        {
            if (left.Value(row, HashSeries.TotalColumn) == right.Value(row, HashSeries.TotalColumn))
            {
                continue;
            }

            var layers = new List<LayerDifference>();
            foreach (string column in shared)
            {
                if (column is HashSeries.TickColumn or HashSeries.TotalColumn)
                {
                    continue;
                }
                string a = left.Value(row, column);
                string b = right.Value(row, column);
                if (a != b)
                {
                    layers.Add(new LayerDifference(column, a, b));
                }
            }

            return new ComparisonResult
            {
                Identical = false,
                FirstDivergentTick = left.Tick(row),
                DivergentLayers = layers,
                LeftOnlyColumns = leftOnly,
                RightOnlyColumns = rightOnly,
                LeftCount = left.Count,
                RightCount = right.Count,
            };
        }

        // 前 common 帧总哈希一致；帧数不等仍不算一致，因为两路跑完的长度本应相同
        return new ComparisonResult
        {
            Identical = left.Count == right.Count,
            FirstDivergentTick = null,
            LeftOnlyColumns = leftOnly,
            RightOnlyColumns = rightOnly,
            LeftCount = left.Count,
            RightCount = right.Count,
        };
    }
}

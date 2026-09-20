using System.Globalization;

namespace PatchParity.Pipeline;

/// <summary>
/// 一次运行产出的逐 tick 分层状态哈希。
/// 列按名字寻址而不按位置：两次构建注册的组件集合未必相同，按位置对齐会把不同的层错配到一起。
/// </summary>
public sealed class HashSeries
{
    private readonly Dictionary<string, int> _columnIndex;
    private readonly List<string[]> _rows;

    private HashSeries(IReadOnlyList<string> columns, Dictionary<string, int> columnIndex, List<string[]> rows)
    {
        Columns = columns;
        _columnIndex = columnIndex;
        _rows = rows;
    }

    /// <summary>表头列名，保持文件中的顺序。</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>帧数，不含表头。</summary>
    public int Count => _rows.Count;

    /// <summary>总哈希列，判定是否一致只看它；分层列用于不一致时定位。</summary>
    public const string TotalColumn = "total";

    public const string TickColumn = "tick";

    /// <summary>组件计数列的前缀与后缀，形如 c:TransformComponent:count。</summary>
    public const string ComponentPrefix = "c:";

    public const string CountSuffix = ":count";

    public static HashSeries Parse(string text)
    {
        var lines = text.Split('\n');
        var columns = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<string[]>();

        bool headerRead = false;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (!headerRead)
            {
                columns.AddRange(line.Split(','));
                for (int i = 0; i < columns.Count; i++)
                {
                    // 重复列名会让按名寻址产生歧义，宁可当场失败
                    if (!index.TryAdd(columns[i], i))
                    {
                        throw new FormatException($"分层哈希表头出现重复列名: {columns[i]}");
                    }
                }
                headerRead = true;
                continue;
            }

            string[] cells = line.Split(',');
            if (cells.Length != columns.Count)
            {
                throw new FormatException(
                    $"第 {rows.Count} 行有 {cells.Length} 个字段，表头有 {columns.Count} 个");
            }
            rows.Add(cells);
        }

        if (!headerRead)
        {
            throw new FormatException("分层哈希文件没有表头");
        }
        if (!index.ContainsKey(TotalColumn) || !index.ContainsKey(TickColumn))
        {
            throw new FormatException($"分层哈希表头缺少 {TickColumn} 或 {TotalColumn} 列");
        }

        return new HashSeries(columns, index, rows);
    }

    public static HashSeries Load(string path) => Parse(File.ReadAllText(path));

    public bool HasColumn(string column) => _columnIndex.ContainsKey(column);

    /// <summary>取第 row 帧、名为 column 的原始值。</summary>
    public string Value(int row, string column)
    {
        if (!_columnIndex.TryGetValue(column, out int col))
        {
            throw new KeyNotFoundException($"没有名为 {column} 的列");
        }
        return _rows[row][col];
    }

    public int Tick(int row) => int.Parse(Value(row, TickColumn), CultureInfo.InvariantCulture);

    /// <summary>该帧全部组件类型的实例数之和。为 0 表示世界是空的。</summary>
    public int LiveComponents(int row)
    {
        int sum = 0;
        foreach (string column in Columns)
        {
            if (column.StartsWith(ComponentPrefix, StringComparison.Ordinal) &&
                column.EndsWith(CountSuffix, StringComparison.Ordinal))
            {
                sum += int.Parse(_rows[row][_columnIndex[column]], CultureInfo.InvariantCulture);
            }
        }
        return sum;
    }

    /// <summary>某个组件类型在该帧的实例数；该列不存在时返回 0。</summary>
    public int ComponentCount(int row, string componentName)
    {
        string column = ComponentPrefix + componentName + CountSuffix;
        return _columnIndex.TryGetValue(column, out int col)
            ? int.Parse(_rows[row][col], CultureInfo.InvariantCulture)
            : 0;
    }

    /// <summary>出现在表头里的全部组件类型名。</summary>
    public IEnumerable<string> ComponentNames()
    {
        foreach (string column in Columns)
        {
            if (column.StartsWith(ComponentPrefix, StringComparison.Ordinal) &&
                column.EndsWith(CountSuffix, StringComparison.Ordinal))
            {
                yield return column.Substring(
                    ComponentPrefix.Length,
                    column.Length - ComponentPrefix.Length - CountSuffix.Length);
            }
        }
    }
}

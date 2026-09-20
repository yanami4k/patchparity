using PatchParity.Pipeline;

namespace PatchParity.Cli;

/// <summary>
/// 比对与自检的命令行入口。编排与缓存尚未接入，此处只暴露已实现的纯逻辑部分。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // 报告含中文，按控制台默认代码页输出会乱码
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向到不支持设置编码的句柄时忽略
        }

        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "compare" => Compare(args),
                "determinism" => Determinism(args),
                "coverage" => Coverage(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("错误: " + e.Message);
            return 9;
        }
    }

    // 退出码：0 一致 / 3 不一致 / 4 自检不通过 / 2 用法错误 / 9 异常
    private static int Compare(string[] args)
    {
        if (args.Length < 3)
        {
            PrintUsage();
            return 2;
        }

        HashSeries left = HashSeries.Load(args[1]);
        HashSeries right = HashSeries.Load(args[2]);
        string[] touched = args.Length > 3
            ? args[3].Split(',', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        CoverageCheck coverage = Preconditions.CheckCoverage(left, touched);
        Console.WriteLine($"覆盖自检: {(coverage.Passed ? "通过" : "不通过")} " +
                          $"末帧组件实例数={coverage.LiveComponents} 强度={Describe(coverage.Strength)}");
        if (coverage.Reason is not null)
        {
            Console.WriteLine("  " + coverage.Reason);
        }

        ComparisonResult result = SeriesComparer.Compare(left, right);
        Console.WriteLine($"帧数: 左={result.LeftCount} 右={result.RightCount}");
        if (result.LeftOnlyColumns.Count > 0 || result.RightOnlyColumns.Count > 0)
        {
            Console.WriteLine($"仅左侧有的列: {result.LeftOnlyColumns.Count}，仅右侧有的列: {result.RightOnlyColumns.Count}");
        }

        if (result.Identical)
        {
            // 覆盖不通过时不得报"一致"：那种一致可能只说明改动点没被执行
            Console.WriteLine(coverage.Passed
                ? "判定: 一致"
                : "判定: 作废（逐帧相同，但覆盖自检不通过，不构成补丁等价的证据）");
            return coverage.Passed ? 0 : 4;
        }

        Console.WriteLine($"判定: 不一致，首个分歧 tick={result.FirstDivergentTick}");
        if (result.DivergedOutsideLayers)
        {
            Console.WriteLine("  所有共有分层一致而总哈希不同：分歧落在分层未覆盖的部分");
        }
        foreach (LayerDifference layer in result.DivergentLayers)
        {
            Console.WriteLine($"  {layer.Layer}  左={layer.Left}  右={layer.Right}");
        }
        Console.WriteLine($"  分歧层数: {result.DivergentLayers.Count}");
        return 3;
    }

    private static int Determinism(string[] args)
    {
        if (args.Length < 3)
        {
            PrintUsage();
            return 2;
        }

        DeterminismCheck check = Preconditions.CheckDeterminism(
            HashSeries.Load(args[1]), HashSeries.Load(args[2]));

        if (check.Passed)
        {
            Console.WriteLine("确定性自检: 通过（两次运行逐 tick 一致）");
            return 0;
        }

        Console.WriteLine($"确定性自检: 不通过，首个分歧 tick={check.FirstDivergentTick}");
        foreach (LayerDifference layer in check.DivergentLayers)
        {
            Console.WriteLine($"  {layer.Layer}  第一次={layer.Left}  第二次={layer.Right}");
        }
        return 4;
    }

    private static int Coverage(string[] args)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 2;
        }

        HashSeries series = HashSeries.Load(args[1]);
        string[] touched = args.Length > 2
            ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        CoverageCheck check = Preconditions.CheckCoverage(series, touched);
        Console.WriteLine($"覆盖自检: {(check.Passed ? "通过" : "不通过")} " +
                          $"末帧组件实例数={check.LiveComponents} 强度={Describe(check.Strength)}");
        if (check.Reason is not null)
        {
            Console.WriteLine("  " + check.Reason);
        }
        return check.Passed ? 0 : 4;
    }

    private static string Describe(CoverageStrength strength) => strength switch
    {
        CoverageStrength.TouchedComponentsPresent => "改动触及的组件在场景中出现过",
        _ => "仅世界非空（不足以说明改动点被执行）",
    };

    private static int Unknown(string command)
    {
        Console.Error.WriteLine("未知命令: " + command);
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            用法:
              patchparity compare <左.csv> <右.csv> [触及的组件,逗号分隔]
              patchparity determinism <第一次.csv> <第二次.csv>
              patchparity coverage <哈希.csv> [触及的组件,逗号分隔]

            退出码: 0 通过 / 3 不一致 / 4 自检不通过 / 2 用法错误 / 9 异常
            """);
    }
}

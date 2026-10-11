using PatchParity.Pipeline;

namespace PatchParity.Cli;

/// <summary>驱动一次完整验证并打印报告。</summary>
public static class VerifyCommand
{
    public static int Run(IReadOnlyList<string> args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            PrintUsage();
            return 2;
        }

        // 与 MCP 会话共用同一把工作区租约：命令行验证同样施加改动、启动 Unity
        using WorkspaceLease lease = WorkspaceLease.ForProject(options.Project);
        if (!lease.TryAcquire(out string? holder))
        {
            Console.Error.WriteLine($"工作区被另一个会话占用（{holder}），同一个 Unity 工程同一时刻只允许一个会话施加改动或构建。");
            return 6;
        }

        // 与 MCP 会话共用同一份改动日志：命令行验证被中断留下的改动，下一个会话能恢复，反之亦然
        IPatchSource patchSource = JournaledPatchSource.ForProject(options.Repository, options.Project);
        if (patchSource.RestoreWorkspace() is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 7;
        }

        var paths = new ToolPaths
        {
            UnityExecutable = options.Unity,
            ProjectPath = options.Project,
            ArtifactRoot = options.Artifacts,
        };
        Directory.CreateDirectory(paths.ArtifactRoot);

        var plan = new VerificationPlan(
            paths,
            new UnityInvoker(paths, new ProcessLauncher()),
            patchSource)
        {
            // 与 MCP 共用同一套缓存键：用 --cache 指向同一目录时，两边可互相复用母包基线
            Cache = options.NoCache ? null : new BaselineCache(options.Cache),
            BaselineKey = options.NoCache ? null : () => CacheKeyBuilder.ForProject(options.Unity, options.Project),
        };

        var change = new ChangeCase
        {
            Name = options.Name,
            PatchPath = options.CasePath,
            Expected = options.Expected,
            TouchedComponents = options.Touched,
            Scenario = new Scenario { Seed = options.Seed, Ticks = options.Ticks, Players = options.Players, Bots = options.Bots },
        };

        Console.WriteLine($"用例 {change.Name}｜场景 {change.Scenario.Name}｜预期 {Describe(change.Expected)}");
        Console.WriteLine(options.NoCache ? "不用缓存，从改动前的源码构建母包基线" : "母包基线按内容哈希缓存：" + options.Cache);
        Console.WriteLine();

        VerificationReport report = plan.Verify(change);
        Print(report);

        bool matchesExpectation = CaseVerdict.Matches(report.Verdict, change.Expected);
        Console.WriteLine();
        Console.WriteLine(matchesExpectation ? "与预期相符" : "与预期不符");
        return report.Verdict switch
        {
            Verdict.Equivalent => 0,
            Verdict.Divergent => 3,
            Verdict.Void => 4,
            Verdict.RebuildFailed => 8,
            _ => 5,
        };
    }

    private static void Print(VerificationReport report)
    {
        if (report.Baseline is { } baseline)
        {
            // 裁决 005：缓存键的组成随报告打印，漏项导致的错误复用才查得出来
            Console.WriteLine(baseline.Hit ? "母包基线: 命中缓存，跳过构建" : "母包基线: 未命中，已构建并写入缓存");
            Console.WriteLine(baseline.Key.Describe());
        }

        foreach (StepResult step in report.Steps)
        {
            string state = step.Succeeded ? "成功" : "失败";
            Console.WriteLine($"  {step.Step,-20} {state}  {step.Elapsed.TotalSeconds,7:F1} 秒");
        }
        Console.WriteLine();

        if (report.Determinism is { } determinism)
        {
            Console.WriteLine(determinism.Passed
                ? "确定性自检: 通过（同场景两次运行逐 tick 一致）"
                : $"确定性自检: 不通过，tick {determinism.FirstDivergentTick} 起不同");
        }
        if (report.Coverage is { } coverage)
        {
            Console.WriteLine($"覆盖自检: {(coverage.Passed ? "通过" : "不通过")} " +
                              $"末帧组件实例数={coverage.LiveComponents}");
        }

        Console.WriteLine();
        switch (report.Verdict)
        {
            case Verdict.Equivalent:
                Console.WriteLine($"判定: 等价（{report.Comparison!.LeftCount} 帧逐 tick 一致）");
                break;
            case Verdict.Divergent:
                Console.WriteLine($"判定: 不等价，首个分歧 tick={report.Comparison!.FirstDivergentTick}");
                foreach (LayerDifference layer in report.Comparison.DivergentLayers)
                {
                    Console.WriteLine($"  {layer.Layer}  整包重编={layer.Left}  打补丁={layer.Right}");
                }
                break;
            case Verdict.PatchFailed:
                Console.WriteLine($"判定: 补丁未能跑到比对阶段（{Describe(report.Failure!.Stage)}）");
                Console.WriteLine($"  {report.Reason}");
                if (report.Failure.Message is not null)
                {
                    Console.WriteLine($"  {report.Failure.Message}");
                }
                break;
            case Verdict.RebuildFailed:
                Console.WriteLine($"判定: 整包重编失败（{Describe(report.Failure!.Stage)}），补丁路径已跑通");
                Console.WriteLine($"  {report.Reason}");
                if (report.Failure.Message is not null)
                {
                    Console.WriteLine($"  {report.Failure.Message}");
                }
                break;
            case Verdict.Void:
                Console.WriteLine("判定: 作废");
                Console.WriteLine($"  {report.Reason}");
                break;
        }
    }


    private static string Describe(ExpectedVerdict expected) => expected switch
    {
        ExpectedVerdict.Safe => "安全",
        ExpectedVerdict.Dangerous => "危险",
        _ => "边界（以实测为准）",
    };

    private static string Describe(FailureStage stage) => stage switch
    {
        FailureStage.Build => "构建期",
        FailureStage.Load => "加载期",
        FailureStage.Runtime => "运行期",
        FailureStage.Mismatch => "哈希分歧",
        _ => "无",
    };

    private static void PrintUsage() => Console.WriteLine("""
        用法:
          patchparity verify --unity <Unity.exe> --project <Unity 工程目录>
                             --artifacts <产物目录> --repo <被测样例仓库>
                             --case <patch 路径> --name <用例名>
                             [--touched 组件A,组件B] [--expect safe|dangerous|boundary]
                             [--seed 42] [--ticks 600] [--players 2] [--bots 0]
                             [--cache <缓存目录，缺省为产物目录下的 baseline-cache>] [--no-cache]

        退出码: 0 一致 / 3 不一致 / 4 作废 / 5 补丁失败 / 8 整包重编失败 / 6 工作区被占用 / 7 工作区有无法恢复的残留 / 2 用法错误
        """);

    private sealed class Options
    {
        public string Unity = "";
        public string Project = "";
        public string Artifacts = "";
        public string Repository = "";
        public string CasePath = "";
        public string Name = "";
        public string[] Touched = Array.Empty<string>();
        public ExpectedVerdict Expected = ExpectedVerdict.Boundary;
        public int Seed = 42;
        public int Ticks = 600;
        public int Players = 2;
        public int Bots;
        public string Cache = "";
        public bool NoCache;

        public static Options? Parse(IReadOnlyList<string> args)
        {
            var o = new Options();
            for (int i = 1; i < args.Count; i++)
            {
                string flag = args[i];
                if (flag == "--no-cache")
                {
                    o.NoCache = true;
                    continue;
                }
                if (i + 1 >= args.Count)
                {
                    return null;
                }
                string value = args[++i];
                switch (flag)
                {
                    case "--unity": o.Unity = value; break;
                    case "--project": o.Project = value; break;
                    case "--artifacts": o.Artifacts = value; break;
                    case "--repo": o.Repository = value; break;
                    case "--cache": o.Cache = value; break;
                    case "--case": o.CasePath = value; break;
                    case "--name": o.Name = value; break;
                    case "--touched": o.Touched = value.Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                    case "--expect":
                        o.Expected = value switch
                        {
                            "safe" => ExpectedVerdict.Safe,
                            "dangerous" => ExpectedVerdict.Dangerous,
                            _ => ExpectedVerdict.Boundary,
                        };
                        break;
                    case "--seed": o.Seed = int.Parse(value); break;
                    case "--ticks": o.Ticks = int.Parse(value); break;
                    case "--players": o.Players = int.Parse(value); break;
                    case "--bots": o.Bots = int.Parse(value); break;
                    default: return null;
                }
            }

            if (o.Cache.Length == 0 && o.Artifacts.Length > 0)
            {
                o.Cache = Path.Combine(o.Artifacts, "baseline-cache");
            }

            bool complete = o.Unity.Length > 0 && o.Project.Length > 0 && o.Artifacts.Length > 0
                            && o.Repository.Length > 0 && o.CasePath.Length > 0 && o.Name.Length > 0;
            return complete ? o : null;
        }
    }
}

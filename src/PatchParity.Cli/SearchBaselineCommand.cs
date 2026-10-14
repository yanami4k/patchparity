using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PatchParity.Pipeline;

namespace PatchParity.Cli;

/// <summary>
/// 随机搜索基线：对一处改动，在公开的参数空间里随机取场景，数要试几次才让母包与整包重编不同（改动可观测）。
/// 与 Agent 的场景搜索对照：Agent 读 diff 推理，这里只看参数说明（裁决 022）。
///
/// 两个包都从缓存取，不在这里构建：先用 verify 跑过这道题，母包基线与整包重编就都按内容键缓存好了。
/// 标准输出最后一行是 JSON 摘要。
/// </summary>
public static class SearchBaselineCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 退出码：0 跑完 / 2 用法错误 / 6 工作区被占用 / 7 工作区不干净 / 10 包不在缓存里
    public static int Run(IReadOnlyList<string> args)
    {
        Options? options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("""
                用法: patchparity search-baseline --unity <Unity.exe> --project <工程> --repo <仓库> --artifacts <产物目录> --cache <缓存目录>
                                                  --case <改动.patch> --name <题号> [--trials 10] [--max-attempts 8] [--seed 1]
                """);
            return 2;
        }

        using WorkspaceLease lease = WorkspaceLease.ForProject(options.Project);
        if (!lease.TryAcquire(out string? holder))
        {
            Console.Error.WriteLine($"工作区被另一个会话占用（{holder}）");
            return 6;
        }
        IPatchSource patchSource = JournaledPatchSource.ForProject(options.Repository, options.Project);
        if (patchSource.RestoreWorkspace() is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 7;
        }

        var cache = new BaselineCache(options.Cache);
        var change = new ChangeCase { Name = options.Name, PatchPath = options.CasePath, Expected = ExpectedVerdict.Boundary };

        // 母包基线的键按未施加改动的源码算，整包重编的按施加之后的算；只算键、不构建
        patchSource.Revert(change);
        CacheLookup baseline = cache.Lookup(CacheKeyBuilder.ForProject(options.Unity, options.Project));
        CacheLookup rebuild;
        patchSource.Apply(change);
        try
        {
            rebuild = cache.Lookup(CacheKeyBuilder.ForProject(options.Unity, options.Project));
        }
        finally
        {
            patchSource.Revert(change);
        }
        foreach ((string what, CacheLookup lookup) in new[] { ("母包基线", baseline), ("整包重编", rebuild) })
        {
            if (!lookup.Hit)
            {
                Console.Error.WriteLine($"{what}不在缓存里（{lookup.Key.Value}）：先用 patchparity verify 跑一次这道题");
                return 10;
            }
        }

        var paths = new ToolPaths { UnityExecutable = options.Unity, ProjectPath = options.Project, ArtifactRoot = options.Artifacts };
        string runsRoot = Path.Combine(options.Artifacts, "search-baseline", options.Name);
        if (Directory.Exists(runsRoot))
        {
            Directory.Delete(runsRoot, recursive: true);
        }
        var search = new RandomSearch(new UnityInvoker(paths, new ProcessLauncher()), baseline.PackageDirectory, rebuild.PackageDirectory, runsRoot);

        Console.WriteLine($"随机搜索 {options.Name}：{options.Trials} 轮，每轮至多 {options.MaxAttempts} 次");
        Console.WriteLine($"  母包基线 {baseline.Key.Value}，整包重编 {rebuild.Key.Value}");
        var trials = new List<SearchTrial>();
        for (int t = 1; t <= options.Trials; t++)
        {
            SearchTrial trial = search.RunTrial(t, options.Seed * 1000 + t, options.MaxAttempts);
            trials.Add(trial);
            string outcome = trial.HitAttempt is { } hit ? $"第 {hit} 次命中：{trial.Scenarios[hit - 1]}" : $"{options.MaxAttempts} 次都没命中";
            Console.WriteLine($"  第 {t} 轮：{outcome}" + (trial.FailedRuns > 0 ? $"（{trial.FailedRuns} 次运行失败）" : ""));
        }

        JsonObject summary = Summarize(options.Name, options.MaxAttempts, trials);
        Directory.CreateDirectory(runsRoot);
        File.WriteAllText(Path.Combine(runsRoot, "summary.json"), summary.ToJsonString(new JsonSerializerOptions(Json) { WriteIndented = true }));
        Console.WriteLine($"命中 {summary["hits"]} / {trials.Count}，命中时平均第 {summary["mean_hit_attempt"]} 次");
        Console.WriteLine(summary.ToJsonString(Json));
        return 0;
    }

    public static JsonObject Summarize(string name, int maxAttempts, IReadOnlyList<SearchTrial> trials)
    {
        int[] hits = trials.Where(t => t.HitAttempt is not null).Select(t => t.HitAttempt!.Value).ToArray();
        var attempts = new JsonArray();
        foreach (SearchTrial t in trials)
        {
            attempts.Add(t.HitAttempt is { } a ? JsonValue.Create(a) : null);
        }
        return new JsonObject
        {
            ["case"] = name,
            ["trials"] = trials.Count,
            ["max_attempts"] = maxAttempts,
            ["hits"] = hits.Length,
            ["hit_rate"] = trials.Count == 0 ? 0 : Math.Round(hits.Length / (double)trials.Count, 3),
            // 只在命中的轮次上平均：没命中的轮次至少要 max_attempts + 1 次，单列命中率
            ["mean_hit_attempt"] = hits.Length == 0 ? null : Math.Round(hits.Average(), 2),
            ["attempts"] = attempts,
            ["failed_runs"] = trials.Sum(t => t.FailedRuns),
        };
    }

    private sealed class Options
    {
        public string Unity = "";
        public string Project = "";
        public string Repository = "";
        public string Artifacts = "";
        public string Cache = "";
        public string CasePath = "";
        public string Name = "";
        public int Trials = 10;
        public int MaxAttempts = 8;
        public int Seed = 1;

        public static Options? Parse(IReadOnlyList<string> args)
        {
            var o = new Options();
            for (int i = 1; i < args.Count; i++)
            {
                if (i + 1 >= args.Count)
                {
                    return null;
                }
                string value = args[++i];
                switch (args[i - 1])
                {
                    case "--unity": o.Unity = value; break;
                    case "--project": o.Project = value; break;
                    case "--repo": o.Repository = value; break;
                    case "--artifacts": o.Artifacts = value; break;
                    case "--cache": o.Cache = value; break;
                    case "--case": o.CasePath = value; break;
                    case "--name": o.Name = value; break;
                    case "--trials": if (!PositiveInt(value, out o.Trials)) return null; break;
                    case "--max-attempts": if (!PositiveInt(value, out o.MaxAttempts)) return null; break;
                    case "--seed": if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out o.Seed)) return null; break;
                    default: return null;
                }
            }
            bool complete = o.Unity != "" && o.Project != "" && o.Repository != "" && o.Artifacts != "" && o.Cache != "" &&
                            o.CasePath != "" && o.Name != "";
            return complete ? o : null;
        }

        private static bool PositiveInt(string value, out int result) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 0;
    }
}

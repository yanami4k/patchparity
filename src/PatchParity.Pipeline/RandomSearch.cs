namespace PatchParity.Pipeline;

/// <summary>
/// 在 list_scenarios 公开的参数空间里随机取场景，代表"不读代码、只看参数说明"的搜索方式，作为 Agent 场景搜索的对照基线。
/// 每个可选参数各以一半的概率保持缺省，否则在 <see cref="Scenario.CustomParameters"/> 写明的范围内均匀取值。
/// 种子固定为缺省的 42：要搜的是玩法参数，不是世界随机数，Agent 也几乎从不改种子。
/// </summary>
public static class ScenarioSampler
{
    public static Scenario Sample(Random rng)
    {
        bool keep() => rng.Next(2) == 0;

        int players = keep() ? 2 : rng.Next(1, 5);
        var scenario = new Scenario { Players = players };
        if (!keep()) scenario = scenario with { Ticks = rng.Next(1, 6001) };
        if (!keep()) scenario = scenario with { Ground = rng.Next(2) == 1 };
        if (!keep()) scenario = scenario with { MoveSpeed = rng.Next(0, 2001) / 100m };
        if (!keep()) scenario = scenario with { Classes = Enumerable.Range(0, players).Select(_ => rng.Next(4)).ToArray() };
        if (!keep())
        {
            scenario = scenario with
            {
                Spawn = Enumerable.Range(0, players).Select(_ => (rng.Next(-180, 181) / 10m, rng.Next(-180, 181) / 10m)).ToArray(),
            };
        }
        if (!keep()) scenario = scenario with { Attack = rng.Next(0, 1001) };
        if (!keep()) scenario = scenario with { Jump = rng.Next(0, 1001) };
        if (!keep()) scenario = scenario with { Skill = rng.Next(0, 1001) };
        if (!keep()) scenario = scenario with { Chase = rng.Next(2) == 1 };
        return scenario;
    }
}

/// <summary>一轮随机搜索：第几次尝试时改动变得可观测（没命中为 null），以及依次试过的场景。</summary>
public sealed record SearchTrial(int Trial, int? HitAttempt, IReadOnlyList<string> Scenarios, int FailedRuns);

/// <summary>
/// 随机搜索基线：拿未打补丁的母包与整包重编的包，反复取随机场景各跑一次，比对逐 tick 分层哈希，
/// 直到两者不同（改动在该场景下可观测）或试满次数。与 Agent 的场景搜索用同一个"可观测"判据（裁决 019）。
/// </summary>
public sealed class RandomSearch
{
    private readonly UnityInvoker _unity;
    private readonly string _baselineDir;
    private readonly string _rebuildDir;
    private readonly string _runsRoot;

    public RandomSearch(UnityInvoker unity, string baselineDir, string rebuildDir, string runsRoot)
    {
        _unity = unity;
        _baselineDir = baselineDir;
        _rebuildDir = rebuildDir;
        _runsRoot = runsRoot;
    }

    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public SearchTrial RunTrial(int trial, int seed, int maxAttempts)
    {
        var rng = new Random(seed);
        var scenarios = new List<string>();
        int failed = 0;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Scenario scenario = ScenarioSampler.Sample(rng);
            scenarios.Add(scenario.ToOptionString());
            string dir = Path.Combine(_runsRoot, $"t{trial}-a{attempt}");
            RunResult baseline = _unity.RunScenario(_baselineDir, Path.Combine(dir, "baseline"), scenario, RunTimeout, "baseline");
            RunResult rebuild = _unity.RunScenario(_rebuildDir, Path.Combine(dir, "rebuild"), scenario, RunTimeout, "rebuild");
            // 跑不完的场景算一次没命中的尝试：Agent 遇到运行失败同样要换场景
            if (!baseline.Succeeded || !rebuild.Succeeded)
            {
                failed++;
                continue;
            }
            ComparisonResult comparison = SeriesComparer.Compare(HashSeries.Load(rebuild.CsvPath!), HashSeries.Load(baseline.CsvPath!));
            if (!comparison.Identical)
            {
                return new SearchTrial(trial, attempt, scenarios, failed);
            }
            // 没命中的尝试不留产物：长场景的哈希文件有几 MB，一道题几十次尝试能攒下几百 MB；命中的那次留作证据
            Directory.Delete(dir, recursive: true);
        }
        return new SearchTrial(trial, null, scenarios, failed);
    }
}

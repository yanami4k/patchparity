using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Pipeline.Tests;

/// <summary>
/// 随机搜索基线：在公开的参数空间里随机取场景，数要试几次才让母包与整包重编不同。
/// 作为 Agent 场景搜索的对照：Agent 读 diff 推理，随机搜索只看参数说明。
/// </summary>
public class RandomSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-search-" + Guid.NewGuid().ToString("N"));

    public RandomSearchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── 取样 ───────────────────────────────────────────────

    [Fact]
    public void 取样出的场景都合法且同一种子取到同一串场景()
    {
        var a = new Random(7);
        var b = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            Scenario s = ScenarioSampler.Sample(a);
            // 取值范围与个数都须过 Parse 的校验，否则 harness 可能静默退回缺省行为。
            // bots、maxEntities、deltaTimeMs 固定不变，调用方给不了，解析前去掉
            string custom = s.ToOptionString().Replace("bots=0;maxEntities=512;deltaTimeMs=25;", "", StringComparison.Ordinal)
                .Replace(";bots=0;maxEntities=512;deltaTimeMs=25", "", StringComparison.Ordinal);
            Assert.Equal(s.ToOptionString(), Scenario.Parse(custom).ToOptionString());
            Assert.Equal(s.ToOptionString(), ScenarioSampler.Sample(b).ToOptionString());
        }
    }

    [Fact]
    public void 每个参数都有时保持缺省有时取别的值()
    {
        var rng = new Random(1);
        var samples = Enumerable.Range(0, 2000).Select(_ => ScenarioSampler.Sample(rng)).ToList();

        Assert.Contains(samples, s => s.Ground);
        Assert.Contains(samples, s => !s.Ground);
        Assert.Contains(samples, s => s.MoveSpeed is null);
        Assert.Contains(samples, s => s.MoveSpeed > 0);
        Assert.Contains(samples, s => s.Classes is null);
        Assert.Contains(samples, s => s.Classes is { } c && c.Contains(3));
        Assert.Contains(samples, s => s.Spawn is null);
        Assert.Contains(samples, s => s.Spawn is not null);
        Assert.Contains(samples, s => s.Skill == 0);
        Assert.Contains(samples, s => s.Skill > 0);
        Assert.Contains(samples, s => s.Ticks == 600);
        Assert.Contains(samples, s => s.Ticks > 3000);
        Assert.Contains(samples, s => s.Players == 1);
        Assert.Contains(samples, s => s.Players == 4);
        Assert.All(samples, s => Assert.Equal(42, s.Seed));
        // 一半的概率加地面：只看参数说明的搜索大约四次里有一次放了地面
        double groundRate = samples.Count(s => s.Ground) / (double)samples.Count;
        Assert.InRange(groundRate, 0.2, 0.3);
    }

    // ── 搜索 ───────────────────────────────────────────────

    private const string Header = "tick,total,c:Item:count,c:Item:hash";
    private const string Plain = Header + "\n0,1,1,10\n1,2,1,20\n";
    private const string Changed = Header + "\n0,1,1,10\n1,9,1,99\n";

    /// <summary>扮演验证包：整包重编的包在有地面的场景下产出不同的哈希，其余场景两边一样。</summary>
    private sealed class GroundSensitiveLauncher : IProcessLauncher
    {
        public List<string> Scenarios { get; } = new();

        public void LaunchDetached(string executable, IReadOnlyList<string> arguments, string? outputLog = null)
        {
            string options = ValueOf(arguments, "-patchparityScenario")!;
            Scenarios.Add(options);
            bool rebuild = executable.Contains("rebuild", StringComparison.Ordinal);
            string csv = rebuild && options.Contains("ground=1", StringComparison.Ordinal) ? Changed : Plain;
            string result = ValueOf(arguments, "-patchparityResult")!;
            Directory.CreateDirectory(Path.GetDirectoryName(result)!);
            File.WriteAllText(ValueOf(arguments, "-patchparityCsv")!, csv);
            File.WriteAllText(result, "OK ticks=2 lastLiveComponents=1\n");
        }

        public bool WaitForArtifact(string artifactPath, TimeSpan timeout) => File.Exists(artifactPath);

        private static string? ValueOf(IReadOnlyList<string> arguments, string name)
        {
            int i = arguments.ToList().IndexOf(name);
            return i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null;
        }
    }

    private RandomSearch Search(IProcessLauncher launcher)
    {
        var paths = new ToolPaths { UnityExecutable = "unity", ProjectPath = Path.Combine(_root, "project"), ArtifactRoot = _root };
        return new RandomSearch(new UnityInvoker(paths, launcher), Path.Combine(_root, "baseline"), Path.Combine(_root, "rebuild"),
            Path.Combine(_root, "runs"));
    }

    [Fact]
    public void 第一个放了地面的场景即命中()
    {
        var launcher = new GroundSensitiveLauncher();

        SearchTrial trial = Search(launcher).RunTrial(1, seed: 3, maxAttempts: 30);

        // 按同一种子重放取样，找出第一个带地面的场景，命中应恰在那一次
        var rng = new Random(3);
        int expected = Enumerable.Range(1, 30).First(_ => ScenarioSampler.Sample(rng).Ground);
        Assert.Equal(expected, trial.HitAttempt);
        Assert.Equal(expected, trial.Scenarios.Count);
        Assert.Contains("ground=1", trial.Scenarios[^1]);
        // 每次尝试在两个包上各跑一次同一场景
        Assert.Equal(2 * expected, launcher.Scenarios.Count);
        // 没命中的尝试不留产物，命中的那次留作证据
        Assert.Equal(new[] { $"t1-a{expected}" }, Directory.GetDirectories(Path.Combine(_root, "runs")).Select(Path.GetFileName));
    }

    [Fact]
    public void 试满次数仍不可观测时记为未命中()
    {
        var launcher = new GroundSensitiveLauncher();
        // 找一个前两次都不带地面的种子
        int seed = Enumerable.Range(0, 1000).First(s =>
        {
            var rng = new Random(s);
            return !ScenarioSampler.Sample(rng).Ground && !ScenarioSampler.Sample(rng).Ground;
        });

        SearchTrial trial = Search(launcher).RunTrial(1, seed, maxAttempts: 2);

        Assert.Null(trial.HitAttempt);
        Assert.Equal(2, trial.Scenarios.Count);
        Assert.Equal(0, trial.FailedRuns);
    }
}

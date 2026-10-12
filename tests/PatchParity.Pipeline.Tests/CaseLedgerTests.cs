using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Pipeline.Tests;

/// <summary>
/// 台账与判定规则：MCP 一路由 Agent 按顺序调用工具，判定只能从服务端记下的事实推出。
/// </summary>
public class CaseLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-ledger-" + Guid.NewGuid().ToString("N"));

    private const string Header = "tick,total,frame,c:ItemComponent:count,c:ItemComponent:hash";
    private static readonly string Patched = Header + "\n0,100,900,1,11\n1,101,901,1,12\n";
    private static readonly string Other = Header + "\n0,100,900,1,11\n1,777,901,1,99\n";
    private static readonly string Empty = Header + "\n0,100,900,0,0\n1,101,901,0,0\n";

    private static readonly string[] Touched = { "ItemComponent" };

    public CaseLedgerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── 按真实顺序记下的一次完整验证 ───────────────────────

    private List<LedgerEntry> FullRun(string rebuildCsv = "", string? patchedAgainCsv = null)
    {
        return new List<LedgerEntry>
        {
            new() { Step = LedgerSteps.BaselineBuild, Ok = true, Package = "base", Seconds = 600 },
            new() { Step = LedgerSteps.PatchCompile, Ok = true, PatchName = "x", Seconds = 90 },
            Run("p1", LedgerSteps.Patched, "base", Patched),
            Run("p2", LedgerSteps.Patched, "base", patchedAgainCsv ?? Patched),
            new() { Step = LedgerSteps.RebuildBuild, Ok = true, Package = "rebuilt", Seconds = 690 },
            Run("r", LedgerSteps.Rebuild, "rebuilt", rebuildCsv.Length > 0 ? rebuildCsv : Patched),
        };
    }

    private LedgerEntry Run(string name, string kind, string package, string csv, string scenario = "default")
    {
        string path = Path.Combine(_root, name + ".csv");
        File.WriteAllText(path, csv);
        return new LedgerEntry
        {
            Step = LedgerSteps.Run, Ok = true, RunName = name, RunKind = kind, Package = package,
            PatchName = kind == LedgerSteps.Patched ? "x" : null, Scenario = scenario, Csv = path, Seconds = 6,
        };
    }

    private static CaseDecision Judge(IReadOnlyList<LedgerEntry> entries)
    {
        (CaseEvidence evidence, string? inconsistency) = CaseLedger.ToEvidence(entries, Touched);
        Assert.Null(inconsistency);
        return CaseVerdict.Decide(evidence);
    }

    [Fact]
    public void 完整且一致的台账判为等价()
    {
        Assert.Equal(Verdict.Equivalent, Judge(FullRun()).Verdict);
    }

    [Fact]
    public void 整包重编与补丁不同则判为不等价并给出分歧tick()
    {
        CaseDecision decision = Judge(FullRun(rebuildCsv: Other));

        Assert.Equal(Verdict.Divergent, decision.Verdict);
        Assert.Equal(1, decision.Comparison!.FirstDivergentTick);
    }

    [Fact]
    public void 补丁编译失败即判为补丁失败不再要求后续步骤()
    {
        var entries = new List<LedgerEntry>
        {
            new() { Step = LedgerSteps.BaselineBuild, Ok = true, Package = "base" },
            new() { Step = LedgerSteps.PatchCompile, Ok = false, PatchName = "x", FailureStage = "Build", Message = "编译错误" },
        };

        CaseDecision decision = Judge(entries);

        Assert.Equal(Verdict.PatchFailed, decision.Verdict);
        Assert.Equal(FailureStage.Build, decision.Failure!.Stage);
    }

    [Fact]
    public void 整包重编失败而补丁路径已跑通时判为整包重编失败()
    {
        // 云主机实测：AOT 程序集引用热更程序集的改动，补丁路径照常跑完，整包重编构建期失败
        var entries = FullRun().Take(4).ToList();
        entries.Add(new LedgerEntry { Step = LedgerSteps.RebuildBuild, Ok = false, Package = "rebuilt", FailureStage = "Build" });

        CaseDecision decision = Judge(entries);

        Assert.Equal(Verdict.RebuildFailed, decision.Verdict);
        Assert.Equal(FailureStage.Build, decision.Failure!.Stage);
    }

    [Fact]
    public void 母包基线构建失败判为作废而非补丁失败()
    {
        // 补丁尚未施加，失败的是环境
        var entries = new List<LedgerEntry> { new() { Step = LedgerSteps.BaselineBuild, Ok = false, FailureStage = "Build" } };

        Assert.Equal(Verdict.Void, Judge(entries).Verdict);
    }

    [Fact]
    public void 补丁运行失败判为补丁失败并保留已知故障()
    {
        var entries = FullRun().Take(2).ToList();
        entries.Add(new LedgerEntry
        {
            Step = LedgerSteps.Run, Ok = false, RunName = "p1", RunKind = LedgerSteps.Patched, Package = "base",
            PatchName = "x", Scenario = "default", FailureStage = "Runtime", KnownFault = "AotGenericNotInstantiated",
        });

        CaseDecision decision = Judge(entries);

        Assert.Equal(Verdict.PatchFailed, decision.Verdict);
        Assert.Equal(KnownFault.AotGenericNotInstantiated, decision.Failure!.Fault);
    }

    // ── 证据不全不得判成一致 ───────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void 少了任何一步都判为证据不全(int stepsDone)
    {
        // Agent 漏了步骤或中途被中止：不得据此给出一致之类的结论
        CaseDecision decision = Judge(FullRun().Take(stepsDone).ToList());

        Assert.Equal(Verdict.Incomplete, decision.Verdict);
        Assert.False(decision.Final);
    }

    [Fact]
    public void 确定性自检不过即作废即使后续步骤齐全()
    {
        Assert.Equal(Verdict.Void, Judge(FullRun(patchedAgainCsv: Other)).Verdict);
    }

    [Fact]
    public void 空世界即覆盖不过而作废()
    {
        var entries = FullRun();
        entries[2] = Run("p1", LedgerSteps.Patched, "base", Empty);
        entries[3] = Run("p2", LedgerSteps.Patched, "base", Empty);

        Assert.Equal(Verdict.Void, Judge(entries).Verdict);
    }

    [Fact]
    public void 记为成功但哈希文件不在则当作运行失败()
    {
        var entries = FullRun();
        File.Delete(entries[5].Csv!);

        // 缺的是整包重编那次运行的哈希：失败在标准答案一侧
        Assert.Equal(Verdict.RebuildFailed, Judge(entries).Verdict);
    }

    [Fact]
    public void 命中缓存的母包基线视为可用()
    {
        var entries = FullRun();
        entries[0] = new LedgerEntry { Step = LedgerSteps.BaselineBuild, Ok = true, Package = "base", CacheHit = true };

        Assert.Equal(Verdict.Equivalent, Judge(entries).Verdict);
    }

    // ── 挑选与一致性 ───────────────────────────────────────

    [Fact]
    public void 补丁运行用的不是母包基线时报证据不一致()
    {
        var entries = FullRun();
        entries[2] = Run("p1", LedgerSteps.Patched, "rebuilt", Patched);

        (_, string? inconsistency) = CaseLedger.ToEvidence(entries, Touched);

        Assert.Contains("不是母包基线", inconsistency);
    }

    [Fact]
    public void 整包重编不在补丁运行的场景下跑过即证据不全()
    {
        // 证据以最后一次补丁运行的场景为准；整包重编只在别的场景下跑过，在这个场景下就还缺它
        var entries = FullRun();
        entries[5] = Run("r", LedgerSteps.Rebuild, "rebuilt", Patched, scenario: "seed-7");

        Assert.Equal(Verdict.Incomplete, Judge(entries).Verdict);
    }

    // ── 可观测性与场景搜索（裁决 019）──────────────────────

    [Fact]
    public void 母包自身与整包重编不同即可观测()
    {
        var entries = FullRun();
        entries.Add(Run("b", LedgerSteps.Baseline, "base", Other));

        CaseDecision decision = Judge(entries);

        Assert.Equal(Verdict.Equivalent, decision.Verdict);
        Assert.True(decision.Observable);
    }

    [Fact]
    public void 母包自身与整包重编相同即不可观测但不要求时照常判定()
    {
        var entries = FullRun();
        entries.Add(Run("b", LedgerSteps.Baseline, "base", Patched));

        CaseDecision decision = Judge(entries);

        Assert.Equal(Verdict.Equivalent, decision.Verdict);
        Assert.False(decision.Observable);
    }

    [Fact]
    public void 要求可观测而不可观测时判作废()
    {
        var entries = FullRun();
        entries.Add(Run("b", LedgerSteps.Baseline, "base", Patched));

        (CaseEvidence evidence, _) = CaseLedger.ToEvidence(entries, Touched);
        CaseDecision decision = CaseVerdict.Decide(evidence, requireObservable: true);

        Assert.Equal(Verdict.Void, decision.Verdict);
        Assert.Contains("不可观测", decision.Reason);
    }

    [Fact]
    public void 要求可观测而缺母包自身的运行时证据不全()
    {
        (CaseEvidence evidence, _) = CaseLedger.ToEvidence(FullRun(), Touched);

        Assert.Equal(Verdict.Incomplete, CaseVerdict.Decide(evidence, requireObservable: true).Verdict);
    }

    [Fact]
    public void 没有母包自身的运行时可观测性为空()
    {
        Assert.Null(Judge(FullRun()).Observable);
    }

    [Fact]
    public void 补丁运行之后在别的场景下的试跑不改变选定的场景()
    {
        // 证据以补丁运行的场景为准，而不是以最后一次运行为准：Agent 跑完补丁后又试了一次别的场景
        var entries = FullRun();
        entries.Add(Run("b", LedgerSteps.Baseline, "base", Other));
        entries.Add(Run("late", LedgerSteps.Baseline, "base", Patched, scenario: "seed-7"));

        Assert.Equal("default", CaseLedger.ChosenScenario(entries));
        Assert.Equal(Verdict.Equivalent, Judge(entries).Verdict);
        Assert.True(Judge(entries).Observable);
    }

    [Fact]
    public void 场景搜索后以补丁运行的场景挑选各次运行()
    {
        // Agent 先在缺省场景下试：改动不可观测；换到有地面的场景：可观测；随后在该场景下跑补丁
        var entries = new List<LedgerEntry>
        {
            new() { Step = LedgerSteps.BaselineBuild, Ok = true, Package = "base" },
            new() { Step = LedgerSteps.RebuildBuild, Ok = true, Package = "rebuilt" },
            Run("b-default", LedgerSteps.Baseline, "base", Patched, scenario: "default"),
            Run("r-default", LedgerSteps.Rebuild, "rebuilt", Patched, scenario: "default"),
            Run("b-ground", LedgerSteps.Baseline, "base", Other, scenario: "ground"),
            Run("r-ground", LedgerSteps.Rebuild, "rebuilt", Patched, scenario: "ground"),
            new() { Step = LedgerSteps.PatchCompile, Ok = true, PatchName = "x" },
            Run("p1", LedgerSteps.Patched, "base", Patched, scenario: "ground"),
            Run("p2", LedgerSteps.Patched, "base", Patched, scenario: "ground"),
        };

        (CaseEvidence evidence, string? inconsistency) = CaseLedger.ToEvidence(entries, Touched);
        CaseDecision decision = CaseVerdict.Decide(evidence, requireObservable: true);

        Assert.Null(inconsistency);
        Assert.Equal("ground", CaseLedger.ChosenScenario(entries));
        Assert.Equal(Verdict.Equivalent, decision.Verdict);
        Assert.True(decision.Observable);
    }

    [Fact]
    public void 母包自身的运行不算补丁运行也不算整包重编()
    {
        var entries = FullRun();
        entries.Insert(1, Run("plain", LedgerSteps.Baseline, "base", Other));

        Assert.Equal(Verdict.Equivalent, Judge(entries).Verdict);
    }

    [Fact]
    public void 只认在最后一次整包重编的包上的运行()
    {
        // 先有一个失败或过期的重编包，再重编一次：比对要用最后那个包的运行
        var entries = FullRun();
        entries.Insert(4, new LedgerEntry { Step = LedgerSteps.RebuildBuild, Ok = true, Package = "stale" });
        entries.Insert(5, Run("old", LedgerSteps.Rebuild, "stale", Other));

        Assert.Equal(Verdict.Equivalent, Judge(entries).Verdict);
    }

    // ── 落盘 ───────────────────────────────────────────────

    [Fact]
    public void 每步写入全部目标且可读回()
    {
        string a = Path.Combine(_root, "worktree", CaseLedger.FileName);
        string b = Path.Combine(_root, "session", CaseLedger.FileName);
        var ledger = new CaseLedger(a, b);

        ledger.Record(new LedgerEntry { Step = LedgerSteps.BaselineBuild, Ok = true, Package = "base", CacheHit = true });
        ledger.Record(new LedgerEntry { Step = LedgerSteps.PatchCompile, Ok = false, PatchName = "x", Message = "坏了" });

        foreach (string path in new[] { a, b })
        {
            IReadOnlyList<LedgerEntry> read = CaseLedger.Load(path);
            Assert.Equal(2, read.Count);
            Assert.True(read[0].CacheHit);
            Assert.Equal("坏了", read[1].Message);
            Assert.NotNull(read[1].At);
        }
        Assert.Empty(Directory.GetFiles(_root, "*.partial", SearchOption.AllDirectories));
    }

    // ── 是否符合预期 ───────────────────────────────────────

    [Theory]
    [InlineData(Verdict.Equivalent, ExpectedVerdict.Safe, null, true)]
    [InlineData(Verdict.Divergent, ExpectedVerdict.Safe, null, false)]
    [InlineData(Verdict.PatchFailed, ExpectedVerdict.Dangerous, null, true)]
    [InlineData(Verdict.Divergent, ExpectedVerdict.Dangerous, null, true)]
    [InlineData(Verdict.Equivalent, ExpectedVerdict.Dangerous, null, false)]
    [InlineData(Verdict.RebuildFailed, ExpectedVerdict.Dangerous, null, true)]
    [InlineData(Verdict.RebuildFailed, ExpectedVerdict.Safe, null, false)]
    [InlineData(Verdict.Equivalent, ExpectedVerdict.Boundary, Verdict.Equivalent, true)]
    [InlineData(Verdict.PatchFailed, ExpectedVerdict.Boundary, Verdict.Equivalent, false)]
    [InlineData(Verdict.Incomplete, ExpectedVerdict.Boundary, null, false)]
    [InlineData(Verdict.Incomplete, ExpectedVerdict.Dangerous, null, false)]
    public void 判定与预期的比对(Verdict verdict, ExpectedVerdict expected, Verdict? measured, bool matches)
    {
        Assert.Equal(matches, CaseVerdict.Matches(verdict, expected, measured));
    }
}

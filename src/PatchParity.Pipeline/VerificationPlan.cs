namespace PatchParity.Pipeline;

/// <summary>最终判定。</summary>
public enum Verdict
{
    /// <summary>两路逐帧一致，且两项前置自检通过。</summary>
    Equivalent,

    /// <summary>两路行为不一致，报告含首个分歧 tick 与分歧层。</summary>
    Divergent,

    /// <summary>补丁路径未能跑到比对阶段：构建、加载或运行期失败。</summary>
    PatchFailed,

    /// <summary>前置自检不通过，比对结果作废。</summary>
    Void,

    /// <summary>证据不全：后面的步骤还没做。只出现在从台账还原的验证里，命令行一路总会跑到定论。</summary>
    Incomplete,

    /// <summary>
    /// 整包重编失败：标准答案一侧构建不出来或跑不完，补丁路径则已跑通。
    /// 这类改动连完整构建都过不了，而热更补丁照常装得上——改动里母包那部分被悄悄丢掉了（如 AOT 程序集引用热更程序集）。
    /// </summary>
    RebuildFailed,
}

public sealed record VerificationReport
{
    public required Verdict Verdict { get; init; }
    public required string CaseName { get; init; }
    public required Scenario Scenario { get; init; }
    public IReadOnlyList<StepResult> Steps { get; init; } = Array.Empty<StepResult>();
    public ComparisonResult? Comparison { get; init; }

    /// <summary>母包基线的缓存键与是否命中。裁决要求键的组成随报告打印。</summary>
    public CacheLookup? Baseline { get; init; }
    public DeterminismCheck? Determinism { get; init; }
    public CoverageCheck? Coverage { get; init; }
    public FailureReport? Failure { get; init; }

    /// <summary>作废或失败的原因，直接面向读报告的人。</summary>
    public string? Reason { get; init; }

    /// <summary>改动在场景下是否可观测；母包在该场景下没跑成时为空。</summary>
    public bool? Observable { get; init; }
}

/// <summary>
/// 一个改动的完整验证。
///
/// 顺序刻意如此：母包基线必须由改动前的源码构建，因为 HybridCLR 生成 AOT 元数据时会扫描热更程序集，
/// 母包并非与改动无关，而是"在某个源码版本下生成的"。补丁路径随后装在该母包上，
/// 整包重编则由改动后的源码全量构建，两者比对才回答得了"打补丁是否等价于整包重编"。
/// </summary>
public sealed class VerificationPlan
{
    private readonly UnityInvoker _unity;
    private readonly ToolPaths _paths;
    private readonly IPatchSource _patch;

    public VerificationPlan(ToolPaths paths, UnityInvoker unity, IPatchSource patch)
    {
        _paths = paths;
        _unity = unity;
        _patch = patch;
    }

    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 母包基线的缓存。命中则跳过基线构建——基线对全部用例是同一个，建一次即可反复使用。
    /// 为空时每次都重建基线。
    /// </summary>
    public BaselineCache? Cache { get; init; }

    /// <summary>计算母包基线缓存键所需的输入。缺省时不启用缓存。</summary>
    public Func<CacheKey>? BaselineKey { get; init; }

    /// <summary>要求改动在场景下可观测，否则判作废（见 <see cref="CaseVerdict.Decide"/>）。</summary>
    public bool RequireObservable { get; init; }

    public VerificationReport Verify(ChangeCase change)
    {
        var steps = new List<StepResult>();
        string baselineDir = Path.Combine(_paths.ArtifactRoot, "baseline-package");
        CacheLookup? baselineLookup = null;
        string rebuildDir = Path.Combine(_paths.ArtifactRoot, "rebuild-package");
        var evidence = new CaseEvidence { TouchedComponents = change.TouchedComponents };

        // 每步之后交给判定规则：第一处失败即定论，不再往下跑。规则本身只在 CaseVerdict 一处
        VerificationReport? Settled(CaseEvidence e)
        {
            evidence = e;
            CaseDecision decision = CaseVerdict.Decide(e, RequireObservable);
            return decision.Final ? Report(change, steps, baselineLookup, decision) : null;
        }

        try
        {
            // 一、母包基线：改动必须未施加
            _patch.Revert(change);

            CacheLookup? lookup;
            if (Cache is not null && BaselineKey is not null)
            {
                lookup = Cache.Lookup(BaselineKey());
                baselineDir = lookup.PackageDirectory;
                baselineLookup = lookup;
            }
            else
            {
                lookup = null;
            }

            if (lookup is not { Hit: true })
            {
                StepResult baseline = _unity.BuildPlayer(baselineDir, BuildTimeout, "baseline-build");
                steps.Add(baseline);
                if (!baseline.Succeeded && lookup is not null)
                {
                    // 构建失败会留下不完整的目录，必须撤销标记，否则下次会命中一个不可用的母包
                    Cache!.Invalidate(lookup.Key);
                }
                else if (lookup is not null)
                {
                    Cache!.MarkComplete(lookup.Key);
                }
                if (Settled(evidence with { BaselineBuild = baseline, BaselineReady = baseline.Succeeded }) is { } failed)
                {
                    return failed;
                }
            }
            else
            {
                evidence = evidence with { BaselineReady = true };
            }

            // 母包自身在该场景下跑一次（几秒）：与整包重编比，判断改动在此场景下是否可观测（裁决 019）。
            // 放在施加改动之前，此时源码与母包一致；失败不影响后续步骤，只是没有可观测性可报
            RunResult baselineRun = _unity.RunScenario(
                baselineDir, Path.Combine(_paths.ArtifactRoot, "run-baseline"), change.Scenario, RunTimeout, "baseline-run");
            steps.Add(baselineRun);
            evidence = evidence with { BaselineRun = baselineRun };

            // 二、补丁路径：施加改动，只重编热更程序集并换进母包基线
            _patch.Apply(change);
            // 补丁编译进独立目录，不写入母包基线——缓存中的包必须保持与其键一致
            string patchDir = Path.Combine(_paths.ArtifactRoot, "patch-" + change.Name);
            string patchDlls = Path.Combine(patchDir, "HotUpdateDlls");
            StepResult compile = _unity.CompileHotUpdate(patchDir, CompileTimeout, "patch-compile");
            steps.Add(compile);
            if (Settled(evidence with { PatchCompile = compile }) is { } afterCompile)
            {
                return afterCompile;
            }

            RunResult patchRun = _unity.RunScenario(
                baselineDir, Path.Combine(_paths.ArtifactRoot, "run-patch"), change.Scenario, RunTimeout, "patch-run", patchDlls);
            steps.Add(patchRun);
            if (Settled(evidence with { PatchRun = patchRun }) is { } afterRun)
            {
                return afterRun;
            }

            // 三、确定性自检与四、覆盖自检：同一包同一场景再跑一次，判定规则随即检查两项
            RunResult patchRunAgain = _unity.RunScenario(
                baselineDir, Path.Combine(_paths.ArtifactRoot, "run-patch-2"), change.Scenario, RunTimeout, "patch-run-repeat", patchDlls);
            steps.Add(patchRunAgain);
            if (Settled(evidence with { PatchRunAgain = patchRunAgain }) is { } afterRepeat)
            {
                return afterRepeat;
            }

            // 五、整包重编：改动已施加，全量构建后跑同一场景
            StepResult rebuild = _unity.BuildPlayer(rebuildDir, BuildTimeout, "rebuild");
            steps.Add(rebuild);
            if (Settled(evidence with { Rebuild = rebuild }) is { } afterRebuild)
            {
                return afterRebuild;
            }

            RunResult rebuildRun = _unity.RunScenario(
                rebuildDir, Path.Combine(_paths.ArtifactRoot, "run-rebuild"), change.Scenario, RunTimeout, "rebuild-run");
            steps.Add(rebuildRun);
            return Settled(evidence with { RebuildRun = rebuildRun })
                   ?? throw new InvalidOperationException("证据已全部到齐却没有定论，判定规则有误");
        }
        finally
        {
            // 无论结论如何都还原源码，避免把改动留给下一个用例
            _patch.Revert(change);
        }
    }

    private static VerificationReport Report(
        ChangeCase change, IReadOnlyList<StepResult> steps, CacheLookup? baseline, CaseDecision decision) =>
        new()
        {
            Verdict = decision.Verdict,
            CaseName = change.Name,
            Scenario = change.Scenario,
            Steps = steps.ToArray(),
            Baseline = baseline,
            Comparison = decision.Comparison,
            Determinism = decision.Determinism,
            Coverage = decision.Coverage,
            Failure = decision.Failure,
            Reason = decision.Reason,
            Observable = decision.Observable,
        };
}

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
}

public sealed record VerificationReport
{
    public required Verdict Verdict { get; init; }
    public required string CaseName { get; init; }
    public required Scenario Scenario { get; init; }
    public IReadOnlyList<StepResult> Steps { get; init; } = Array.Empty<StepResult>();
    public ComparisonResult? Comparison { get; init; }
    public DeterminismCheck? Determinism { get; init; }
    public CoverageCheck? Coverage { get; init; }
    public FailureReport? Failure { get; init; }

    /// <summary>作废或失败的原因，直接面向读报告的人。</summary>
    public string? Reason { get; init; }
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
    /// 母包基线是否已就绪且对应改动前的源码。为真时跳过基线构建，
    /// 这是内容哈希缓存的接入点——同一基线可服务大量用例。
    /// </summary>
    public bool ReuseBaselinePackage { get; init; }

    public VerificationReport Verify(ChangeCase change)
    {
        var steps = new List<StepResult>();
        string baselineDir = Path.Combine(_paths.ArtifactRoot, "baseline-package");
        string rebuildDir = Path.Combine(_paths.ArtifactRoot, "rebuild-package");

        try
        {
            // 一、母包基线：改动必须未施加
            _patch.Revert(change);
            if (!ReuseBaselinePackage)
            {
                StepResult baseline = _unity.BuildPlayer(baselineDir, BuildTimeout, "baseline-build");
                steps.Add(baseline);
                if (!baseline.Succeeded)
                {
                    return Failed(change, steps, baseline.Failure, "母包基线构建失败，无从验证");
                }
            }

            // 二、补丁路径：施加改动，只重编热更程序集并换进母包基线
            _patch.Apply(change);
            StepResult compile = _unity.CompileHotUpdate(baselineDir, CompileTimeout, "patch-compile");
            steps.Add(compile);
            if (!compile.Succeeded)
            {
                return Failed(change, steps, compile.Failure, "补丁编译失败");
            }

            RunResult patchRun = _unity.RunScenario(
                baselineDir, Path.Combine(_paths.ArtifactRoot, "run-patch"), change.Scenario, RunTimeout, "patch-run");
            steps.Add(patchRun);
            if (!patchRun.Succeeded)
            {
                return Failed(change, steps, patchRun.Failure, "补丁装上母包后未能跑完场景");
            }

            // 三、确定性自检：同一包同一场景再跑一次，必须逐帧一致
            RunResult patchRunAgain = _unity.RunScenario(
                baselineDir, Path.Combine(_paths.ArtifactRoot, "run-patch-2"), change.Scenario, RunTimeout, "patch-run-repeat");
            steps.Add(patchRunAgain);
            if (!patchRunAgain.Succeeded)
            {
                return Failed(change, steps, patchRunAgain.Failure, "确定性自检的第二次运行失败");
            }

            HashSeries patchSeries = HashSeries.Load(patchRun.CsvPath!);
            DeterminismCheck determinism = Preconditions.CheckDeterminism(
                patchSeries, HashSeries.Load(patchRunAgain.CsvPath!));
            if (!determinism.Passed)
            {
                return new VerificationReport
                {
                    Verdict = Verdict.Void,
                    CaseName = change.Name,
                    Scenario = change.Scenario,
                    Steps = steps,
                    Determinism = determinism,
                    Reason = $"确定性自检不通过：同一场景两次运行在 tick {determinism.FirstDivergentTick} 起不同，比对结果作废",
                };
            }

            // 四、覆盖自检：改动点是否真被执行到
            CoverageCheck coverage = Preconditions.CheckCoverage(patchSeries, change.TouchedComponents);
            if (!coverage.Passed)
            {
                return new VerificationReport
                {
                    Verdict = Verdict.Void,
                    CaseName = change.Name,
                    Scenario = change.Scenario,
                    Steps = steps,
                    Determinism = determinism,
                    Coverage = coverage,
                    Reason = "覆盖自检不通过：" + coverage.Reason,
                };
            }

            // 五、整包重编：改动已施加，全量构建后跑同一场景
            StepResult rebuild = _unity.BuildPlayer(rebuildDir, BuildTimeout, "rebuild");
            steps.Add(rebuild);
            if (!rebuild.Succeeded)
            {
                return Failed(change, steps, rebuild.Failure, "整包重编失败，无法取得比对基准");
            }

            RunResult rebuildRun = _unity.RunScenario(
                rebuildDir, Path.Combine(_paths.ArtifactRoot, "run-rebuild"), change.Scenario, RunTimeout, "rebuild-run");
            steps.Add(rebuildRun);
            if (!rebuildRun.Succeeded)
            {
                return Failed(change, steps, rebuildRun.Failure, "整包重编的产物未能跑完场景");
            }

            ComparisonResult comparison = SeriesComparer.Compare(
                HashSeries.Load(rebuildRun.CsvPath!), patchSeries);

            return new VerificationReport
            {
                Verdict = comparison.Identical ? Verdict.Equivalent : Verdict.Divergent,
                CaseName = change.Name,
                Scenario = change.Scenario,
                Steps = steps,
                Comparison = comparison,
                Determinism = determinism,
                Coverage = coverage,
            };
        }
        finally
        {
            // 无论结论如何都还原源码，避免把改动留给下一个用例
            _patch.Revert(change);
        }
    }

    private static VerificationReport Failed(
        ChangeCase change, IReadOnlyList<StepResult> steps, FailureReport failure, string reason) =>
        new()
        {
            Verdict = Verdict.PatchFailed,
            CaseName = change.Name,
            Scenario = change.Scenario,
            Steps = steps,
            Failure = failure,
            Reason = reason,
        };
}

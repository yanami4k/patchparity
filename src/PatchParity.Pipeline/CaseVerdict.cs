namespace PatchParity.Pipeline;

/// <summary>
/// 一个改动的各步证据。命令行按顺序逐步跑出来，MCP 一路则从台账里还原；判定规则只有 <see cref="CaseVerdict"/> 一处。
/// 某步为空表示尚未做到，规则据此区分"失败在这一步"与"还没走到这一步"。
/// </summary>
public sealed record CaseEvidence
{
    /// <summary>母包基线的构建。命中缓存时为空，由 <see cref="BaselineReady"/> 表示可用。</summary>
    public StepResult? BaselineBuild { get; init; }

    /// <summary>母包基线可用：本次构建成功，或命中缓存。</summary>
    public bool BaselineReady { get; init; }

    public StepResult? PatchCompile { get; init; }
    public RunResult? PatchRun { get; init; }

    /// <summary>确定性自检的第二次运行：同一包、同一补丁、同一场景。</summary>
    public RunResult? PatchRunAgain { get; init; }

    public StepResult? Rebuild { get; init; }
    public RunResult? RebuildRun { get; init; }

    public IReadOnlyCollection<string> TouchedComponents { get; init; } = Array.Empty<string>();
}

/// <summary>按现有证据得出的判定。<see cref="Final"/> 为假时证据不全，后面的步骤还没做。</summary>
public sealed record CaseDecision
{
    public required Verdict Verdict { get; init; }
    public string? Reason { get; init; }
    public FailureReport? Failure { get; init; }
    public DeterminismCheck? Determinism { get; init; }
    public CoverageCheck? Coverage { get; init; }
    public ComparisonResult? Comparison { get; init; }

    public bool Final => Verdict != Verdict.Incomplete;
}

/// <summary>
/// 判定规则：按验证的固定顺序逐项检查证据，第一处失败即定论；全部到齐才比对。
///
/// 规则只此一处，命令行与 MCP 台账共用，不让同一份证据在两条路上得出不同结论。
/// 顺序的理由见 <see cref="VerificationPlan"/>：母包基线、补丁路径、确定性自检、覆盖自检、整包重编，最后比对。
/// </summary>
public static class CaseVerdict
{
    public static CaseDecision Decide(CaseEvidence e)
    {
        // 一、母包基线
        if (e.BaselineBuild is { Succeeded: false } baseline)
        {
            // 此时补丁尚未施加，严格说是环境问题而非补丁问题；沿用命令行一直以来的判法，是否改判另议
            return Failed(baseline.Failure, "母包基线构建失败，无从验证");
        }
        if (!e.BaselineReady)
        {
            return Incomplete("还没有可用的母包基线");
        }

        // 二、补丁路径
        if (e.PatchCompile is null)
        {
            return Incomplete("补丁还没有编译");
        }
        if (!e.PatchCompile.Succeeded)
        {
            return Failed(e.PatchCompile.Failure, "补丁编译失败");
        }
        if (e.PatchRun is null)
        {
            return Incomplete("补丁还没有装上母包运行");
        }
        if (!e.PatchRun.Succeeded)
        {
            return Failed(e.PatchRun.Failure, "补丁装上母包后未能跑完场景");
        }

        // 三、确定性自检：同一场景再跑一次，必须逐帧一致
        if (e.PatchRunAgain is null)
        {
            return Incomplete("确定性自检的第二次运行还没做");
        }
        if (!e.PatchRunAgain.Succeeded)
        {
            return Failed(e.PatchRunAgain.Failure, "确定性自检的第二次运行失败");
        }

        HashSeries patchSeries = HashSeries.Load(e.PatchRun.CsvPath!);
        DeterminismCheck determinism = Preconditions.CheckDeterminism(patchSeries, HashSeries.Load(e.PatchRunAgain.CsvPath!));
        if (!determinism.Passed)
        {
            return new CaseDecision
            {
                Verdict = Verdict.Void,
                Determinism = determinism,
                Reason = $"确定性自检不通过：同一场景两次运行在 tick {determinism.FirstDivergentTick} 起不同，比对结果作废",
            };
        }

        // 四、覆盖自检：改动点是否真被执行到
        CoverageCheck coverage = Preconditions.CheckCoverage(patchSeries, e.TouchedComponents);
        if (!coverage.Passed)
        {
            return new CaseDecision
            {
                Verdict = Verdict.Void,
                Determinism = determinism,
                Coverage = coverage,
                Reason = "覆盖自检不通过：" + coverage.Reason,
            };
        }

        // 五、整包重编
        if (e.Rebuild is null)
        {
            return Incomplete("还没有整包重编", determinism, coverage);
        }
        if (!e.Rebuild.Succeeded)
        {
            return Failed(e.Rebuild.Failure, "整包重编失败，无法取得比对基准");
        }
        if (e.RebuildRun is null)
        {
            return Incomplete("整包重编的产物还没有运行", determinism, coverage);
        }
        if (!e.RebuildRun.Succeeded)
        {
            return Failed(e.RebuildRun.Failure, "整包重编的产物未能跑完场景");
        }

        ComparisonResult comparison = SeriesComparer.Compare(HashSeries.Load(e.RebuildRun.CsvPath!), patchSeries);
        return new CaseDecision
        {
            Verdict = comparison.Identical ? Verdict.Equivalent : Verdict.Divergent,
            Determinism = determinism,
            Coverage = coverage,
            Comparison = comparison,
        };
    }

    /// <summary>判定是否符合用例的预期。边界类给出实测结论时须与之相同，未给时只要求有定论。</summary>
    public static bool Matches(Verdict verdict, ExpectedVerdict expected, Verdict? measured = null) => expected switch
    {
        ExpectedVerdict.Safe => verdict == Verdict.Equivalent,
        ExpectedVerdict.Dangerous => verdict is Verdict.Divergent or Verdict.PatchFailed,
        _ => measured is { } m ? verdict == m : verdict is not (Verdict.Incomplete or Verdict.Void),
    };

    private static CaseDecision Failed(FailureReport failure, string reason) =>
        new() { Verdict = Verdict.PatchFailed, Failure = failure, Reason = reason };

    private static CaseDecision Incomplete(string reason, DeterminismCheck? determinism = null, CoverageCheck? coverage = null) =>
        new() { Verdict = Verdict.Incomplete, Reason = "证据不全：" + reason, Determinism = determinism, Coverage = coverage };
}

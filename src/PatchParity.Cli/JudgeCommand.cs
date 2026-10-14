using System.Text.Json;
using System.Text.Json.Nodes;
using PatchParity.Pipeline;

namespace PatchParity.Cli;

/// <summary>
/// 从 MCP 会话的台账判定一个改动，并与预期比对。供题目的验证脚本（verify.mjs）调用。
///
/// 判定用与命令行验证同一套规则（<see cref="CaseVerdict"/>），不采信 Agent 自己的说法（裁决 009）。
/// 预期结论与改动触及的组件只经命令行参数给出，不写进题面，Agent 无从迎合。
/// 标准输出最后一行是一个 JSON 对象，patchparity eval 会把它原样收进结果文件。
/// </summary>
public static class JudgeCommand
{
    // 退出码：0 符合预期 / 1 不符合 / 2 用法错误
    public static int Run(IReadOnlyList<string> args)
    {
        Options? options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("""
                用法: patchparity judge <台账> --expect safe|dangerous|boundary|unreachable [--require-observable]
                                         [--measured equivalent|divergent|patch_failed] [--touched 组件A,组件B] [--case 用例名]
                                         [--min-scenarios 3]
                """);
            return 2;
        }

        var summary = new JsonObject
        {
            ["case"] = options.Case,
            ["expected"] = options.Expected.ToString().ToLowerInvariant(),
            ["measured"] = options.Measured is { } m ? Name(m) : null,
        };

        if (!File.Exists(options.Ledger))
        {
            // 服务端一步都没做完就不会有台账：Agent 没调工具，或服务端没起来
            Console.WriteLine("没有台账：" + options.Ledger);
            summary["verdict"] = "no_ledger";
            summary["matches"] = false;
            Console.WriteLine(summary.ToJsonString());
            return 1;
        }

        IReadOnlyList<LedgerEntry> entries = CaseLedger.Load(options.Ledger);
        if (options.Expected == ExpectedVerdict.Unreachable)
        {
            return JudgeUnreachable(entries, options, summary);
        }
        (CaseEvidence evidence, string? inconsistency) = CaseLedger.ToEvidence(entries, options.Touched);
        CaseDecision decision = inconsistency is null
            ? CaseVerdict.Decide(evidence, options.RequireObservable)
            : new CaseDecision { Verdict = Verdict.Incomplete, Reason = "证据不一致：" + inconsistency };
        bool matches = CaseVerdict.Matches(decision.Verdict, options.Expected, options.Measured);

        foreach (LedgerEntry e in entries)
        {
            string what = e.Step == LedgerSteps.Run ? $"{e.Step}/{e.RunKind} {e.RunName}" : e.Step;
            string state = e.Ok ? (e.CacheHit ? "命中缓存" : "成功") : "失败";
            Console.WriteLine($"  {what,-32} {state}  {e.Seconds,7:F1} 秒");
        }
        Console.WriteLine($"判定: {Name(decision.Verdict)}" + (decision.Reason is null ? "" : $"（{decision.Reason}）"));
        Console.WriteLine(matches ? "与预期相符" : "与预期不符");

        summary["verdict"] = Name(decision.Verdict);
        summary["matches"] = matches;
        summary["reason"] = decision.Reason;
        summary["observable"] = decision.Observable;
        summary["require_observable"] = options.RequireObservable;
        summary["scenario"] = CaseLedger.ChosenScenario(entries);
        summary["first_divergent_tick"] = decision.Comparison?.FirstDivergentTick;
        var layers = new JsonArray();
        foreach (LayerDifference layer in decision.Comparison?.DivergentLayers ?? Array.Empty<LayerDifference>())
        {
            // 须先转成 JsonNode：泛型的 Add<string> 存的是待序列化的值，输出时要靠反射解析器，下面的选项没有配它，
            // 分歧层一非空就抛异常（云上经 Pigeon 判"不等价"的题时实测：判定已算出，摘要却输出不了，题目被记为失败）
            layers.Add((JsonNode)layer.Layer);
        }
        summary["divergent_layers"] = layers;
        summary["failure_stage"] = decision.Failure?.Stage.ToString();
        summary["known_fault"] = decision.Failure?.Fault.ToString();
        summary["baseline_cache_hit"] = entries.LastOrDefault(e => e.Step == LedgerSteps.BaselineBuild)?.CacheHit;
        summary["steps"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
        {
            ["step"] = e.Step,
            ["run_kind"] = e.RunKind,
            ["ok"] = e.Ok,
            ["seconds"] = e.Seconds,
            ["cache_hit"] = e.CacheHit,
        }).ToArray());
        summary["total_seconds"] = Math.Round(entries.Sum(e => e.Seconds), 1);
        Console.WriteLine(summary.ToJsonString(new JsonSerializerOptions
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return matches ? 0 : 1;
    }

    /// <summary>
    /// 反例题（裁决 022）：改动落在执行不到的代码上。答对的条件是试过至少 min 个不同场景、每个场景下母包与整包重编都逐帧一致。
    /// 任一场景可观测说明改动其实执行得到——那是出题错了，或工具出了问题，判为不符合；试得太少也不符合，不能凭一两次就下"执行不到"的结论。
    /// </summary>
    private static int JudgeUnreachable(IReadOnlyList<LedgerEntry> entries, Options options, JsonObject summary)
    {
        IReadOnlyList<(string Scenario, bool Observable)> scenarios = CaseLedger.ObservabilityByScenario(entries);
        string[] observable = scenarios.Where(s => s.Observable).Select(s => s.Scenario).ToArray();
        string verdict = observable.Length > 0 ? "observable"
            : scenarios.Count < options.MinScenarios ? "insufficient"
            : "unobservable";
        bool matches = verdict == "unobservable";

        foreach ((string scenario, bool isObservable) in scenarios)
        {
            Console.WriteLine($"  {(isObservable ? "可观测" : "不可观测")}  {scenario}");
        }
        Console.WriteLine(verdict switch
        {
            "observable" => $"判定: 可观测（{observable.Length} 个场景下母包与整包重编不同）",
            "insufficient" => $"判定: 试得不够（{scenarios.Count} 个场景，至少要 {options.MinScenarios} 个）",
            _ => $"判定: 不可观测（{scenarios.Count} 个场景下母包与整包重编都逐帧一致）",
        });
        Console.WriteLine(matches ? "与预期相符" : "与预期不符");

        summary["verdict"] = verdict;
        summary["matches"] = matches;
        summary["observable"] = observable.Length > 0;
        summary["scenarios_tried"] = scenarios.Count;
        summary["min_scenarios"] = options.MinScenarios;
        var list = new JsonArray();
        foreach (string s in observable)
        {
            list.Add((JsonNode)s);
        }
        summary["observable_scenarios"] = list;
        summary["total_seconds"] = Math.Round(entries.Sum(e => e.Seconds), 1);
        Console.WriteLine(summary.ToJsonString(new JsonSerializerOptions
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return matches ? 0 : 1;
    }

    private static string Name(Verdict verdict) => verdict switch
    {
        Verdict.Equivalent => "equivalent",
        Verdict.Divergent => "divergent",
        Verdict.PatchFailed => "patch_failed",
        Verdict.RebuildFailed => "rebuild_failed",
        Verdict.Void => "void",
        _ => "incomplete",
    };

    private sealed class Options
    {
        public string Ledger = "";
        public ExpectedVerdict Expected = ExpectedVerdict.Boundary;
        public bool HasExpected;
        public Verdict? Measured;
        public string[] Touched = Array.Empty<string>();
        public string Case = "";

        /// <summary>要求改动在所选场景下可观测，场景生成类题目用（裁决 019）。</summary>
        public bool RequireObservable;

        /// <summary>反例题至少要试过几个不同场景才能下"不可观测"的结论。</summary>
        public int MinScenarios = 3;

        public static Options? Parse(IReadOnlyList<string> args)
        {
            if (args.Count < 2)
            {
                return null;
            }
            var o = new Options { Ledger = args[1] };
            for (int i = 2; i < args.Count; i += 2)
            {
                // 不带值的开关
                if (args[i] == "--require-observable")
                {
                    o.RequireObservable = true;
                    i--;
                    continue;
                }
                if (i + 1 >= args.Count)
                {
                    return null;
                }
                string value = args[i + 1];
                switch (args[i])
                {
                    case "--expect":
                        o.HasExpected = true;
                        o.Expected = value switch
                        {
                            "safe" => ExpectedVerdict.Safe,
                            "dangerous" => ExpectedVerdict.Dangerous,
                            "boundary" => ExpectedVerdict.Boundary,
                            "unreachable" => ExpectedVerdict.Unreachable,
                            _ => throw new ArgumentException("未知的预期：" + value),
                        };
                        break;
                    case "--measured":
                        o.Measured = value switch
                        {
                            "equivalent" => Verdict.Equivalent,
                            "divergent" => Verdict.Divergent,
                            "patch_failed" => Verdict.PatchFailed,
                            "rebuild_failed" => Verdict.RebuildFailed,
                            _ => throw new ArgumentException("实测结论只能是 equivalent、divergent、patch_failed 或 rebuild_failed：" + value),
                        };
                        break;
                    case "--touched":
                        o.Touched = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        break;
                    case "--case":
                        o.Case = value;
                        break;
                    case "--min-scenarios":
                        if (!int.TryParse(value, out o.MinScenarios) || o.MinScenarios < 1)
                        {
                            return null;
                        }
                        break;
                    default:
                        return null;
                }
            }
            // 必须给出预期：不给预期就无所谓判对判错
            return o.HasExpected ? o : null;
        }
    }
}

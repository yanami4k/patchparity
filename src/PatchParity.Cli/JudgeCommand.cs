using System.Text.Json;
using System.Text.Json.Nodes;
using PatchParity.Pipeline;

namespace PatchParity.Cli;

/// <summary>
/// 从 MCP 会话的台账判定一个改动，并与预期比对。供 pigeon eval 的验证脚本调用。
///
/// 判定用与命令行验证同一套规则（<see cref="CaseVerdict"/>），不采信 Agent 自己的说法（裁决 009）。
/// 预期结论与改动触及的组件只经命令行参数给出，不写进题面，Agent 无从迎合。
/// 标准输出最后一行是一个 JSON 对象，pigeon eval 会把它原样收进结果文件。
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
                用法: patchparity judge <台账> --expect safe|dangerous|boundary [--require-observable]
                                         [--measured equivalent|divergent|patch_failed] [--touched 组件A,组件B] [--case 用例名]
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
                    default:
                        return null;
                }
            }
            // 必须给出预期：不给预期就无所谓判对判错
            return o.HasExpected ? o : null;
        }
    }
}

using System.Text.Json.Nodes;
using PatchParity.Cli;
using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Cli.Tests;

/// <summary>
/// judge 子命令：题目的验证脚本靠它的退出码判分，靠它最后一行 JSON 收集结果。
/// 命令行此前没有测试，曾在接口改动后编译不过而无人察觉。
/// </summary>
[Collection("控制台输出")]
public class JudgeCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-judge-" + Guid.NewGuid().ToString("N"));
    private const string Csv = "tick,total,c:ItemComponent:count,c:ItemComponent:hash\n0,1,1,10\n1,2,1,20\n";

    public JudgeCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Ledger => Path.Combine(_root, CaseLedger.FileName);

    /// <summary>写一份按真实顺序走完、补丁与整包重编逐帧一致的台账。</summary>
    private void WriteEquivalentLedger()
    {
        string csv = Path.Combine(_root, "run.csv");
        File.WriteAllText(csv, Csv);
        var ledger = new CaseLedger(Ledger);
        ledger.Record(new LedgerEntry { Step = LedgerSteps.BaselineBuild, Ok = true, Package = "base", CacheHit = true });
        ledger.Record(new LedgerEntry { Step = LedgerSteps.PatchCompile, Ok = true, PatchName = "x", Seconds = 90 });
        foreach (string name in new[] { "p1", "p2" })
        {
            ledger.Record(new LedgerEntry
            {
                Step = LedgerSteps.Run, Ok = true, RunKind = LedgerSteps.Patched, RunName = name, Package = "base",
                PatchName = "x", Scenario = "default", Csv = csv, Seconds = 6,
            });
        }
        ledger.Record(new LedgerEntry { Step = LedgerSteps.RebuildBuild, Ok = true, Package = "rebuilt", Seconds = 690 });
        ledger.Record(new LedgerEntry
        {
            Step = LedgerSteps.Run, Ok = true, RunKind = LedgerSteps.Rebuild, RunName = "r", Package = "rebuilt",
            Scenario = "default", Csv = csv, Seconds = 6,
        });
    }

    private static (int Code, JsonObject Summary) Judge(params string[] args)
    {
        TextWriter original = Console.Out;
        var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            int code = JudgeCommand.Run(new[] { "judge" }.Concat(args).ToArray());
            string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            JsonObject summary = lines.Length > 0 && lines[^1].StartsWith('{') ? JsonNode.Parse(lines[^1])!.AsObject() : new JsonObject();
            return (code, summary);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void 符合预期时退出码为0且末行是判定摘要()
    {
        WriteEquivalentLedger();

        (int code, JsonObject summary) = Judge(Ledger, "--expect", "safe", "--touched", "ItemComponent", "--case", "c1");

        Assert.Equal(0, code);
        Assert.Equal("equivalent", summary["verdict"]!.GetValue<string>());
        Assert.True(summary["matches"]!.GetValue<bool>());
        Assert.True(summary["baseline_cache_hit"]!.GetValue<bool>());
        Assert.Equal(90 + 6 + 6 + 690 + 6.0, summary["total_seconds"]!.GetValue<double>());
        Assert.Equal(6, summary["steps"]!.AsArray().Count);
    }

    [Fact]
    public void 判为不等价时摘要带上分歧tick与分歧层()
    {
        // 云上经 Pigeon 实测：判定为不等价时输出摘要抛异常，退出码 9，危险题被误记为失败
        string patched = Path.Combine(_root, "patched.csv");
        string rebuilt = Path.Combine(_root, "rebuilt.csv");
        File.WriteAllText(patched, Csv);
        File.WriteAllText(rebuilt, "tick,total,c:ItemComponent:count,c:ItemComponent:hash\n0,1,1,10\n1,9,1,99\n");
        var ledger = new CaseLedger(Ledger);
        ledger.Record(new LedgerEntry { Step = LedgerSteps.BaselineBuild, Ok = true, Package = "base", CacheHit = true });
        ledger.Record(new LedgerEntry { Step = LedgerSteps.PatchCompile, Ok = true, PatchName = "x" });
        foreach (string name in new[] { "p1", "p2" })
        {
            ledger.Record(new LedgerEntry
            {
                Step = LedgerSteps.Run, Ok = true, RunKind = LedgerSteps.Patched, RunName = name, Package = "base",
                PatchName = "x", Scenario = "default", Csv = patched,
            });
        }
        ledger.Record(new LedgerEntry { Step = LedgerSteps.RebuildBuild, Ok = true, Package = "rebuilt" });
        ledger.Record(new LedgerEntry
        {
            Step = LedgerSteps.Run, Ok = true, RunKind = LedgerSteps.Rebuild, RunName = "r", Package = "rebuilt",
            Scenario = "default", Csv = rebuilt,
        });

        (int code, JsonObject summary) = Judge(Ledger, "--expect", "dangerous", "--touched", "ItemComponent");

        Assert.Equal(0, code);
        Assert.Equal("divergent", summary["verdict"]!.GetValue<string>());
        Assert.Equal(1, summary["first_divergent_tick"]!.GetValue<int>());
        Assert.Contains("c:ItemComponent:hash", summary["divergent_layers"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    /// <summary>在等价台账的基础上补一次未打补丁的母包运行；csv 与整包重编相同即不可观测。</summary>
    private void AddBaselineRun(string csv)
    {
        string path = Path.Combine(_root, "baseline-run.csv");
        File.WriteAllText(path, csv);
        var entries = CaseLedger.Load(Ledger).ToList();
        var ledger = new CaseLedger(Ledger);
        foreach (LedgerEntry e in entries)
        {
            ledger.Record(e);
        }
        ledger.Record(new LedgerEntry
        {
            Step = LedgerSteps.Run, Ok = true, RunKind = LedgerSteps.Baseline, RunName = "b", Package = "base",
            Scenario = "default", Csv = path,
        });
    }

    [Fact]
    public void 要求可观测而不可观测时判作废且不符合预期()
    {
        WriteEquivalentLedger();
        AddBaselineRun(Csv);

        (int code, JsonObject summary) = Judge(Ledger, "--expect", "safe", "--touched", "ItemComponent", "--require-observable");

        Assert.Equal(1, code);
        Assert.Equal("void", summary["verdict"]!.GetValue<string>());
        Assert.False(summary["observable"]!.GetValue<bool>());
        Assert.True(summary["require_observable"]!.GetValue<bool>());
    }

    [Fact]
    public void 要求可观测且可观测时照常判定并报出场景()
    {
        WriteEquivalentLedger();
        AddBaselineRun(Csv.Replace("1,2,1,20", "1,5,1,77"));

        (int code, JsonObject summary) = Judge(Ledger, "--expect", "safe", "--touched", "ItemComponent", "--require-observable");

        Assert.Equal(0, code);
        Assert.Equal("equivalent", summary["verdict"]!.GetValue<string>());
        Assert.True(summary["observable"]!.GetValue<bool>());
        Assert.Equal("default", summary["scenario"]!.GetValue<string>());
    }

    // ── 反例题（裁决 022）──────────────────────────────────

    /// <summary>母包基线与整包重编各建一次，再在给定的几个场景下各跑一次；changed 里的场景两边不同。</summary>
    private void WriteSearchLedger(int scenarios, params int[] changed)
    {
        string same = Path.Combine(_root, "same.csv");
        string other = Path.Combine(_root, "other.csv");
        File.WriteAllText(same, Csv);
        File.WriteAllText(other, "tick,total,c:ItemComponent:count,c:ItemComponent:hash\n0,1,1,10\n1,9,1,99\n");
        var ledger = new CaseLedger(Ledger);
        ledger.Record(new LedgerEntry { Step = LedgerSteps.BaselineBuild, Ok = true, Package = "base" });
        ledger.Record(new LedgerEntry { Step = LedgerSteps.PatchCompile, Ok = true, PatchName = "x" });
        ledger.Record(new LedgerEntry { Step = LedgerSteps.RebuildBuild, Ok = true, Package = "rebuilt" });
        for (int i = 1; i <= scenarios; i++)
        {
            ledger.Record(new LedgerEntry
            {
                Step = LedgerSteps.Run, Ok = true, RunKind = LedgerSteps.Baseline, RunName = $"baseline-{i}", Package = "base",
                Scenario = $"s{i}", Csv = same,
            });
            ledger.Record(new LedgerEntry
            {
                Step = LedgerSteps.Run, Ok = true, RunKind = LedgerSteps.Rebuild, RunName = $"rebuild-{i}", Package = "rebuilt",
                Scenario = $"s{i}", Csv = changed.Contains(i) ? other : same,
            });
        }
    }

    [Fact]
    public void 反例题试够场景且都不可观测即相符()
    {
        WriteSearchLedger(3);

        (int code, JsonObject summary) = Judge(Ledger, "--expect", "unreachable", "--case", "unreachable-a");

        Assert.Equal(0, code);
        Assert.Equal("unobservable", summary["verdict"]!.GetValue<string>());
        Assert.Equal(3, summary["scenarios_tried"]!.GetValue<int>());
    }

    [Fact]
    public void 反例题试得不够不能下结论()
    {
        WriteSearchLedger(2);

        (int code, JsonObject summary) = Judge(Ledger, "--expect", "unreachable");

        Assert.Equal(1, code);
        Assert.Equal("insufficient", summary["verdict"]!.GetValue<string>());
        Assert.Equal(0, Judge(Ledger, "--expect", "unreachable", "--min-scenarios", "2").Code);
    }

    [Fact]
    public void 反例题有场景可观测即不符()
    {
        WriteSearchLedger(4, changed: 3);

        (int code, JsonObject summary) = Judge(Ledger, "--expect", "unreachable");

        Assert.Equal(1, code);
        Assert.Equal("observable", summary["verdict"]!.GetValue<string>());
        Assert.Equal(new[] { "s3" }, summary["observable_scenarios"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void 不符合预期时退出码为1()
    {
        WriteEquivalentLedger();

        (int code, JsonObject summary) = Judge(Ledger, "--expect", "dangerous", "--touched", "ItemComponent");

        Assert.Equal(1, code);
        Assert.False(summary["matches"]!.GetValue<bool>());
    }

    [Fact]
    public void 边界题按实测结论比对()
    {
        WriteEquivalentLedger();

        Assert.Equal(0, Judge(Ledger, "--expect", "boundary", "--measured", "equivalent", "--touched", "ItemComponent").Code);
        Assert.Equal(1, Judge(Ledger, "--expect", "boundary", "--measured", "patch_failed", "--touched", "ItemComponent").Code);
    }

    [Fact]
    public void 改动点不在世界里时判为作废而不是一致()
    {
        WriteEquivalentLedger();

        (int code, JsonObject summary) = Judge(Ledger, "--expect", "safe", "--touched", "NoSuchComponent");

        Assert.Equal(1, code);
        Assert.Equal("void", summary["verdict"]!.GetValue<string>());
    }

    [Fact]
    public void 没有台账时判为不符合()
    {
        (int code, JsonObject summary) = Judge(Path.Combine(_root, "missing.json"), "--expect", "safe");

        Assert.Equal(1, code);
        Assert.Equal("no_ledger", summary["verdict"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ledger.json")]
    [InlineData("ledger.json|--touched|A")]
    [InlineData("ledger.json|--expect")]
    public void 不给预期或参数不全即为用法错误(string args)
    {
        Assert.Equal(2, Judge(args.Split('|', StringSplitOptions.RemoveEmptyEntries)).Code);
    }
}

using System.Text.Json.Nodes;
using PatchParity.Cli;
using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Cli.Tests;

/// <summary>
/// judge 子命令：pigeon eval 的验证脚本靠它的退出码判分，靠它最后一行 JSON 收集结果。
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

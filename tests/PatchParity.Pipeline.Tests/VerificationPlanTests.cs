using Xunit;
using PatchParity.Pipeline;

namespace PatchParity.Pipeline.Tests;

/// <summary>
/// 用假的启动器驱动编排，检验步骤顺序、自检插入点与失败早停，不实际调用 Unity。
/// </summary>
public class VerificationPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakePatchSource _patch = new();

    private const string Header =
        "tick,total,frame,c:ItemComponent:count,c:ItemComponent:hash";

    private static string Csv(params string[] rows) => Header + "\n" + string.Join("\n", rows) + "\n";

    private static readonly string LiveWorld = Csv("0,100,900,1,11", "1,101,901,1,12");
    private static readonly string LiveWorldOther = Csv("0,100,900,1,11", "1,777,901,1,99");
    private static readonly string EmptyWorld = Csv("0,100,900,0,0", "1,101,901,0,0");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private VerificationPlan Plan(FakeLauncher launcher, bool reuseBaseline = false)
    {
        var paths = new ToolPaths
        {
            UnityExecutable = "unity",
            ProjectPath = Path.Combine(_root, "project"),
            ArtifactRoot = _root,
        };
        Directory.CreateDirectory(_root);
        return new VerificationPlan(paths, new UnityInvoker(paths, launcher), _patch)
        {
            ReuseBaselinePackage = reuseBaseline,
        };
    }

    private static ChangeCase Case() => new()
    {
        Name = "sample",
        PatchPath = "cases/sample.patch",
        Expected = ExpectedVerdict.Safe,
        TouchedComponents = new[] { "ItemComponent" },
    };

    [Fact]
    public void 两路一致且自检通过时判为等价()
    {
        var launcher = new FakeLauncher(_root)
            .BuildSucceeds()
            .CompileSucceeds()
            .RunSucceeds(LiveWorld);

        VerificationReport report = Plan(launcher).Verify(Case());

        Assert.Equal(Verdict.Equivalent, report.Verdict);
        Assert.True(report.Determinism!.Passed);
        Assert.True(report.Coverage!.Passed);
    }

    [Fact]
    public void 步骤顺序为基线构建补丁编译补丁运行两次后整包重编()
    {
        var launcher = new FakeLauncher(_root)
            .BuildSucceeds()
            .CompileSucceeds()
            .RunSucceeds(LiveWorld);

        VerificationReport report = Plan(launcher).Verify(Case());

        Assert.Equal(
            new[] { "baseline-build", "patch-compile", "patch-run", "patch-run-repeat", "rebuild", "rebuild-run" },
            report.Steps.Select(s => s.Step).ToArray());
    }

    [Fact]
    public void 复用母包基线时跳过基线构建()
    {
        var launcher = new FakeLauncher(_root)
            .BuildSucceeds()
            .CompileSucceeds()
            .RunSucceeds(LiveWorld);

        VerificationReport report = Plan(launcher, reuseBaseline: true).Verify(Case());

        Assert.DoesNotContain("baseline-build", report.Steps.Select(s => s.Step));
        Assert.Equal(Verdict.Equivalent, report.Verdict);
    }

    [Fact]
    public void 确定性自检不通过时判定作废且不做整包重编()
    {
        // 同一包两次运行结果不同：比对无意义，且不该再花几分钟去整包重编
        var launcher = new FakeLauncher(_root)
            .BuildSucceeds()
            .CompileSucceeds()
            .RunSequence(LiveWorld, LiveWorldOther);

        VerificationReport report = Plan(launcher).Verify(Case());

        Assert.Equal(Verdict.Void, report.Verdict);
        Assert.False(report.Determinism!.Passed);
        Assert.DoesNotContain("rebuild", report.Steps.Select(s => s.Step));
    }

    [Fact]
    public void 空世界时判定作废而不是等价()
    {
        var launcher = new FakeLauncher(_root)
            .BuildSucceeds()
            .CompileSucceeds()
            .RunSucceeds(EmptyWorld);

        VerificationReport report = Plan(launcher).Verify(Case());

        Assert.Equal(Verdict.Void, report.Verdict);
        Assert.False(report.Coverage!.Passed);
        Assert.DoesNotContain("rebuild", report.Steps.Select(s => s.Step));
    }

    [Fact]
    public void 补丁运行失败时报出失败档位且不继续()
    {
        var launcher = new FakeLauncher(_root)
            .BuildSucceeds()
            .CompileSucceeds()
            .RunFails("FAIL TypeLoadException: Could not load type 'Brawler.GameMenu' from assembly 'Brawler.View'.");

        VerificationReport report = Plan(launcher).Verify(Case());

        Assert.Equal(Verdict.PatchFailed, report.Verdict);
        Assert.Equal(FailureStage.Load, report.Failure!.Stage);
        Assert.Equal(KnownFault.StrippedTypeMissing, report.Failure.Fault);
    }

    [Fact]
    public void 两路行为不一致时报出分歧位置()
    {
        var launcher = new FakeLauncher(_root)
            .BuildSucceeds()
            .CompileSucceeds()
            // 补丁两次运行一致，整包重编的结果与之不同
            .RunSequence(LiveWorld, LiveWorld, LiveWorldOther);

        VerificationReport report = Plan(launcher).Verify(Case());

        Assert.Equal(Verdict.Divergent, report.Verdict);
        Assert.Equal(1, report.Comparison!.FirstDivergentTick);
        Assert.Contains(report.Comparison.DivergentLayers, d => d.Layer == "c:ItemComponent:hash");
    }

    [Fact]
    public void 无论结论如何都会还原改动()
    {
        var launcher = new FakeLauncher(_root)
            .BuildSucceeds()
            .CompileSucceeds()
            .RunSucceeds(LiveWorld);

        Plan(launcher).Verify(Case());

        Assert.False(_patch.Applied);
        Assert.True(_patch.ApplyCount > 0);
    }

    [Fact]
    public void 基线构建失败时立即停止()
    {
        var launcher = new FakeLauncher(_root).BuildFails();

        VerificationReport report = Plan(launcher).Verify(Case());

        Assert.Equal(Verdict.PatchFailed, report.Verdict);
        Assert.Single(report.Steps);
    }

    private sealed class FakePatchSource : IPatchSource
    {
        public bool Applied { get; private set; }
        public int ApplyCount { get; private set; }

        public void Apply(ChangeCase change)
        {
            Applied = true;
            ApplyCount++;
        }

        public void Revert(ChangeCase change) => Applied = false;
    }

    /// <summary>按被启动的可执行文件与参数判断这是哪一步，并写出相应产物。</summary>
    private sealed class FakeLauncher : IProcessLauncher
    {
        private readonly string _root;
        private readonly List<string> _runOutputs = new();
        private int _runIndex;
        private bool _buildOk;
        private bool _compileOk;
        private string? _runFailure;

        public FakeLauncher(string root) => _root = root;

        public FakeLauncher BuildSucceeds() { _buildOk = true; return this; }
        public FakeLauncher BuildFails() { _buildOk = false; return this; }
        public FakeLauncher CompileSucceeds() { _compileOk = true; return this; }

        public FakeLauncher RunSucceeds(string csv)
        {
            _runOutputs.Clear();
            _runOutputs.Add(csv);
            return this;
        }

        public FakeLauncher RunSequence(params string[] csvs)
        {
            _runOutputs.Clear();
            _runOutputs.AddRange(csvs);
            return this;
        }

        public FakeLauncher RunFails(string resultLine) { _runFailure = resultLine; return this; }

        public void LaunchDetached(string executable, IReadOnlyList<string> arguments)
        {
            if (executable.EndsWith("Verify.exe", StringComparison.Ordinal))
            {
                WriteRun(arguments);
                return;
            }

            string method = ValueOf(arguments, "-executeMethod") ?? "";
            if (method.Contains("BuildVerifyPlayer", StringComparison.Ordinal))
            {
                string output = ValueOf(arguments, "-patchparityOutput")!;
                Directory.CreateDirectory(output);
                File.WriteAllText(
                    Path.Combine(output, "verify-build.txt"),
                    _buildOk ? "result=Succeeded\nseconds=1.0\nerrors=0\n" : "result=Failed\nerrors=1\n");
                if (!_buildOk)
                {
                    File.WriteAllText(Path.Combine(_root, ValueOf(arguments, "-logFile") is null ? "x.log" : "ignored.log"), "");
                }
            }
            else if (method.Contains("CompileHotUpdate", StringComparison.Ordinal))
            {
                string output = ValueOf(arguments, "-patchparityOutput")!;
                Directory.CreateDirectory(output);
                File.WriteAllText(
                    Path.Combine(output, "patch-compile.txt"),
                    _compileOk ? "compileSeconds=1.0\n" : "ERROR 编译失败\n");
            }
        }

        private void WriteRun(IReadOnlyList<string> arguments)
        {
            string resultPath = ValueOf(arguments, "-patchparityResult")!;
            string csvPath = ValueOf(arguments, "-patchparityCsv")!;
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);

            if (_runFailure is not null)
            {
                File.WriteAllText(resultPath, _runFailure + "\n");
                return;
            }

            string csv = _runOutputs[Math.Min(_runIndex, _runOutputs.Count - 1)];
            _runIndex++;
            File.WriteAllText(csvPath, csv);
            File.WriteAllText(resultPath, "OK ticks=2 lastLiveComponents=1\n");
        }

        public bool WaitForArtifact(string artifactPath, TimeSpan timeout) => File.Exists(artifactPath);

        private static string? ValueOf(IReadOnlyList<string> arguments, string name)
        {
            for (int i = 0; i < arguments.Count - 1; i++)
            {
                if (arguments[i] == name)
                {
                    return arguments[i + 1];
                }
            }
            return null;
        }
    }
}

using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Pipeline.Tests;

/// <summary>
/// 启动与读取 Unity 时的两处健壮性，均为云主机上经 Pigeon 实跑时踩到的：
/// 调用方只给了精简的环境变量，Unity 找不到 MSVC；构建失败后日志仍被 Unity 开着，读取抛异常。
/// </summary>
public class ChildProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-child-" + Guid.NewGuid().ToString("N"));

    public ChildProcessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── 环境变量 ───────────────────────────────────────────

    [Fact]
    public void 精简环境里缺的标准变量被补上()
    {
        // Pigeon 透传给 MCP 服务端的大致就是这些，ProgramFiles(x86) 不在其中
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = @"C:\Windows\System32",
            ["SYSTEMROOT"] = @"C:\Windows",
            ["USERPROFILE"] = @"C:\Users\someone",
        };

        ChildEnvironment.FillMissing(environment);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), environment["ProgramFiles(x86)"]);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), environment["ProgramFiles"]);
        Assert.False(string.IsNullOrEmpty(environment["NUMBER_OF_PROCESSORS"]));
    }

    [Fact]
    public void 已有的变量不被覆盖且不分大小写()
    {
        var environment = new Dictionary<string, string?> { ["PROGRAMFILES(X86)"] = @"D:\custom" };

        ChildEnvironment.FillMissing(environment);

        Assert.Equal(@"D:\custom", environment["PROGRAMFILES(X86)"]);
        Assert.False(environment.ContainsKey("ProgramFiles(x86)"));
    }

    // ── 读取仍被写着的文件 ─────────────────────────────────

    [Fact]
    public void 构建失败而日志仍被占着时照样归类失败原因()
    {
        // 编辑器写出"构建失败"的报告后还要一阵才退出，日志仍开着；此前读取即抛异常，任务以异常收场
        string output = Path.Combine(_root, "out");
        string log = Path.Combine(_root, "build.log");
        using var editorLog = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        byte[] text = System.Text.Encoding.UTF8.GetBytes(
            "Unity.IL2CPP.Bee.BuildLogic.ToolchainNotFoundException: IL2CPP C++ code builder is unable to build C++ code.\n");
        editorLog.Write(text);
        editorLog.Flush();

        var launcher = new FailingBuildLauncher(output);
        var paths = new ToolPaths { UnityExecutable = "unity", ProjectPath = Path.Combine(_root, "project"), ArtifactRoot = _root };
        var unity = new UnityInvoker(paths, launcher) { EditorLockGrace = TimeSpan.Zero };

        StepResult step = unity.BuildPlayer(output, TimeSpan.FromSeconds(5), "build");

        Assert.False(step.Succeeded);
        Assert.Equal(FailureStage.Build, step.Failure.Stage);
    }

    // ── 子进程的输出 ───────────────────────────────────────

    [Fact]
    public void 给了输出文件时子进程的两路输出都写进文件()
    {
        string log = Path.Combine(_root, "child.log");

        new ProcessLauncher().LaunchDetached("cmd.exe", new[] { "/c", "echo to-stdout& echo to-stderr 1>&2" }, log);

        // 文件在两路都读到结尾时才关闭；等到能独占打开，说明写完了
        string text = "";
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None);
                text = new StreamReader(stream).ReadToEnd();
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
        Assert.Contains("to-stdout", text);
        Assert.Contains("to-stderr", text);
    }

    [Fact]
    public void 验证包的日志留在运行目录里不进调用方的输出()
    {
        // 云上实测：命令行的结论夹在几百行引擎启动日志中间
        var launcher = new RecordingLauncher();
        var paths = new ToolPaths { UnityExecutable = "unity", ProjectPath = Path.Combine(_root, "project"), ArtifactRoot = _root };
        var unity = new UnityInvoker(paths, launcher);
        string runDir = Path.Combine(_root, "run");

        unity.RunScenario(Path.Combine(_root, "package"), runDir, new Scenario(), TimeSpan.Zero);

        Assert.Equal(Path.Combine(runDir, "player-stdout.log"), launcher.OutputLog);
        int logFile = launcher.Arguments.ToList().IndexOf("-logFile");
        Assert.True(logFile >= 0);
        Assert.Equal(Path.Combine(runDir, "player.log"), launcher.Arguments[logFile + 1]);
    }

    private sealed class RecordingLauncher : IProcessLauncher
    {
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
        public string? OutputLog { get; private set; }

        public void LaunchDetached(string executable, IReadOnlyList<string> arguments, string? outputLog = null)
        {
            Arguments = arguments;
            OutputLog = outputLog;
        }

        public bool WaitForArtifact(string artifactPath, TimeSpan timeout) => false;
    }

    /// <summary>写出"构建失败"的报告，日志由测试自己占着。</summary>
    private sealed class FailingBuildLauncher : IProcessLauncher
    {
        private readonly string _output;
        public FailingBuildLauncher(string output) => _output = output;

        public void LaunchDetached(string executable, IReadOnlyList<string> arguments, string? outputLog = null)
        {
            Directory.CreateDirectory(_output);
            File.WriteAllText(Path.Combine(_output, "verify-build.txt"), "result=Failed\nerrors=1\n");
        }

        public bool WaitForArtifact(string artifactPath, TimeSpan timeout) => File.Exists(artifactPath);
    }
}

namespace PatchParity.Pipeline;

/// <summary>被验证工程与工具链的位置。</summary>
public sealed record ToolPaths
{
    public required string UnityExecutable { get; init; }

    /// <summary>被测样例的 Unity 工程目录。</summary>
    public required string ProjectPath { get; init; }

    /// <summary>产物与日志的根目录。</summary>
    public required string ArtifactRoot { get; init; }
}

/// <summary>一步操作的结果。</summary>
public record StepResult
{
    public required string Step { get; init; }
    public required bool Succeeded { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>产物报告的全文，失败时用于归类。</summary>
    public string? Report { get; init; }

    public FailureReport Failure { get; init; } = new() { Stage = FailureStage.None };
}

/// <summary>
/// 以命令行驱动 Unity。三类操作各自把结果写成一个产物文件，等待该文件出现即为完成。
/// </summary>
public sealed class UnityInvoker
{
    private readonly ToolPaths _paths;
    private readonly IProcessLauncher _launcher;

    public UnityInvoker(ToolPaths paths, IProcessLauncher launcher)
    {
        _paths = paths;
        _launcher = launcher;
    }

    /// <summary>
    /// 启动编辑器前，等工程上的另一个 Unity 退出的最长时间。上一步的编辑器写出产物后还要几秒才退出，
    /// 紧接着的下一步须稍等；超过此时长仍被占用，即是残留的编辑器，立即判失败而不是等到构建超时。
    /// </summary>
    public TimeSpan EditorLockGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>整包构建无头验证包。内部会先跑 HybridCLR 的生成流程，耗时以分钟计。</summary>
    public StepResult BuildPlayer(string outputDir, TimeSpan timeout, string stepName = "build")
    {
        string artifact = Path.Combine(outputDir, "verify-build.txt");
        string log = Path.Combine(_paths.ArtifactRoot, stepName + ".log");

        // 旧产物不清会让等待立即返回，把上一轮的结果当成这一轮的
        DeleteIfExists(artifact);

        if (WaitForEditorLock(stepName) is { } blocked)
        {
            return blocked;
        }

        var started = DateTime.UtcNow;
        _launcher.LaunchDetached(_paths.UnityExecutable, new[]
        {
            "-batchmode", "-nographics", "-accept-apiupdate",
            "-projectPath", _paths.ProjectPath,
            "-executeMethod", "PatchParity.EditorTools.PatchParityVerifyBuild.BuildVerifyPlayer",
            "-patchparityOutput", outputDir,
            "-logFile", log,
        });

        bool appeared = _launcher.WaitForArtifact(artifact, timeout);
        var elapsed = DateTime.UtcNow - started;

        if (!appeared)
        {
            return new StepResult
            {
                Step = stepName,
                Succeeded = false,
                Elapsed = elapsed,
                Failure = ClassifyBuildLog(log),
            };
        }

        string report = ReadShared(artifact);
        // 构建报告返回 Succeeded 不等于产物可用，故此处只判构建本身，可用性由后续运行确认
        bool ok = report.Contains("result=Succeeded", StringComparison.Ordinal);
        return new StepResult
        {
            Step = stepName,
            Succeeded = ok,
            Elapsed = elapsed,
            Report = report,
            Failure = ok ? new FailureReport { Stage = FailureStage.None } : ClassifyBuildLog(log),
        };
    }

    /// <summary>
    /// 只重编热更程序集，产物写进 patchDir 下的 HotUpdateDlls，这是补丁路径的全部动作。
    /// patchDir 不得是缓存中的包目录：写进去会使缓存条目的内容与其键不再一致。
    /// </summary>
    public StepResult CompileHotUpdate(string patchDir, TimeSpan timeout, string stepName = "patch-compile")
    {
        string artifact = Path.Combine(_paths.ArtifactRoot, "patch-compile.txt");
        string log = Path.Combine(_paths.ArtifactRoot, stepName + ".log");

        DeleteIfExists(artifact);

        if (WaitForEditorLock(stepName) is { } blocked)
        {
            return blocked;
        }

        var started = DateTime.UtcNow;
        _launcher.LaunchDetached(_paths.UnityExecutable, new[]
        {
            "-batchmode", "-nographics", "-accept-apiupdate",
            "-projectPath", _paths.ProjectPath,
            "-executeMethod", "PatchParity.EditorTools.PatchParityPatch.CompileHotUpdate",
            "-patchparityTarget", patchDir,
            "-patchparityOutput", _paths.ArtifactRoot,
            "-logFile", log,
        });

        bool appeared = _launcher.WaitForArtifact(artifact, timeout);
        var elapsed = DateTime.UtcNow - started;

        if (!appeared)
        {
            return new StepResult
            {
                Step = stepName,
                Succeeded = false,
                Elapsed = elapsed,
                Failure = ClassifyBuildLog(log),
            };
        }

        string report = ReadShared(artifact);
        bool ok = !report.Contains("ERROR", StringComparison.Ordinal);
        return new StepResult
        {
            Step = stepName,
            Succeeded = ok,
            Elapsed = elapsed,
            Report = report,
            Failure = ok ? new FailureReport { Stage = FailureStage.None } : new FailureReport { Stage = FailureStage.Build, Message = report },
        };
    }

    /// <summary>
    /// 运行验证包，产出逐 tick 分层哈希。
    /// hotUpdateDir 为补丁目录：给定时从该目录加载热更程序集，可执行文件与 AOT 元数据仍取自包本身。
    /// 这样"母包加补丁"无需改动包——缓存里的包内容必须与其键一致，不得被补丁写入。
    /// </summary>
    public RunResult RunScenario(
        string packageDir, string runDir, Scenario scenario, TimeSpan timeout,
        string stepName = "run", string? hotUpdateDir = null)
    {
        string resultPath = Path.Combine(runDir, "result.txt");
        string csvPath = Path.Combine(runDir, "hashes.csv");

        Directory.CreateDirectory(runDir);
        DeleteIfExists(resultPath);
        DeleteIfExists(csvPath);

        var started = DateTime.UtcNow;
        _launcher.LaunchDetached(Path.Combine(packageDir, "Verify.exe"), new[]
        {
            "-batchmode", "-nographics",
            "-patchparityResult", resultPath,
            "-patchparityCsv", csvPath,
            "-patchparityDlls", hotUpdateDir ?? Path.Combine(packageDir, "HotUpdateDlls"),
            "-patchparityAotDlls", Path.Combine(packageDir, "AotDlls"),
            "-patchparityScenario", scenario.ToOptionString(),
        });

        bool appeared = _launcher.WaitForArtifact(resultPath, timeout);
        var elapsed = DateTime.UtcNow - started;

        if (!appeared)
        {
            return new RunResult
            {
                Step = stepName,
                Succeeded = false,
                Elapsed = elapsed,
                Failure = new FailureReport { Stage = FailureStage.Runtime, Message = "运行超时，未产出结果文件" },
            };
        }

        string resultLine = ReadShared(resultPath).Split('\n')[0].TrimEnd('\r');
        FailureReport failure = FailureClassifier.ClassifyRun(resultLine);
        bool ok = failure.Stage == FailureStage.None && File.Exists(csvPath);

        return new RunResult
        {
            Step = stepName,
            Succeeded = ok,
            Elapsed = elapsed,
            Report = resultLine,
            Failure = failure,
            CsvPath = ok ? csvPath : null,
        };
    }

    /// <summary>
    /// 工程被另一个 Unity 占用时，新启动的编辑器会立即退出、不产出任何产物，
    /// 而等待方以产物为完成信号，只能干等到超时（整包构建为半小时）。故启动前先查，宽限期过后仍被占用即失败。
    /// </summary>
    private StepResult? WaitForEditorLock(string stepName)
    {
        DateTime deadline = DateTime.UtcNow + EditorLockGrace;
        while (UnityProjectLock.IsHeld(_paths.ProjectPath))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return new StepResult
                {
                    Step = stepName,
                    Succeeded = false,
                    Failure = new FailureReport
                    {
                        Stage = FailureStage.Build,
                        Message = $"工程被另一个 Unity 进程占用（{UnityProjectLock.LockfilePath(_paths.ProjectPath)}），" +
                                  $"等待 {EditorLockGrace.TotalSeconds:F0} 秒仍未释放。可能是之前的会话中途退出后残留的编辑器，需先结束它",
                    },
                };
            }
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
        return null;
    }

    private static FailureReport ClassifyBuildLog(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return new FailureReport { Stage = FailureStage.Build, Message = "没有产出日志" };
        }
        FailureReport report = FailureClassifier.ClassifyBuild(ReadShared(logPath));
        return report.Stage == FailureStage.None
            ? new FailureReport { Stage = FailureStage.Build, Message = "构建未产出报告文件" }
            : report;
    }

    /// <summary>
    /// 读 Unity 写的文件时与它共享句柄。编辑器写出构建报告后还要一阵才退出，期间日志仍开着；
    /// 按缺省方式打开会因"被另一进程使用"而失败（云主机实测：构建失败后归类日志时抛出，任务以异常收场，失败原因丢失）。
    /// </summary>
    internal static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

public sealed record RunResult : StepResult
{
    /// <summary>成功时为逐 tick 哈希文件的路径。</summary>
    public string? CsvPath { get; init; }
}

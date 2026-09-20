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

    /// <summary>整包构建无头验证包。内部会先跑 HybridCLR 的生成流程，耗时以分钟计。</summary>
    public StepResult BuildPlayer(string outputDir, TimeSpan timeout, string stepName = "build")
    {
        string artifact = Path.Combine(outputDir, "verify-build.txt");
        string log = Path.Combine(_paths.ArtifactRoot, stepName + ".log");

        // 旧产物不清会让等待立即返回，把上一轮的结果当成这一轮的
        DeleteIfExists(artifact);

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

        string report = File.ReadAllText(artifact);
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

    /// <summary>只重编热更程序集并替换进已有母包，这是补丁路径的全部动作。</summary>
    public StepResult CompileHotUpdate(string targetPackageDir, TimeSpan timeout, string stepName = "patch-compile")
    {
        string artifact = Path.Combine(_paths.ArtifactRoot, "patch-compile.txt");
        string log = Path.Combine(_paths.ArtifactRoot, stepName + ".log");

        DeleteIfExists(artifact);

        var started = DateTime.UtcNow;
        _launcher.LaunchDetached(_paths.UnityExecutable, new[]
        {
            "-batchmode", "-nographics", "-accept-apiupdate",
            "-projectPath", _paths.ProjectPath,
            "-executeMethod", "PatchParity.EditorTools.PatchParityPatch.CompileHotUpdate",
            "-patchparityTarget", targetPackageDir,
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

        string report = File.ReadAllText(artifact);
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

    /// <summary>运行验证包，产出逐 tick 分层哈希。</summary>
    public RunResult RunScenario(string packageDir, string runDir, Scenario scenario, TimeSpan timeout, string stepName = "run")
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
            "-patchparityDlls", Path.Combine(packageDir, "HotUpdateDlls"),
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

        string resultLine = File.ReadLines(resultPath).FirstOrDefault() ?? "";
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

    private static FailureReport ClassifyBuildLog(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return new FailureReport { Stage = FailureStage.Build, Message = "没有产出日志" };
        }
        FailureReport report = FailureClassifier.ClassifyBuild(File.ReadAllText(logPath));
        return report.Stage == FailureStage.None
            ? new FailureReport { Stage = FailureStage.Build, Message = "构建未产出报告文件" }
            : report;
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

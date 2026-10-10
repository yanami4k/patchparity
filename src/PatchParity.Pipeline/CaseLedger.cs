using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchParity.Pipeline;

/// <summary>台账里的一步。字段名即落盘的 JSON 键。</summary>
public sealed record LedgerEntry
{
    /// <summary><see cref="LedgerSteps"/> 之一。</summary>
    public required string Step { get; init; }
    public required bool Ok { get; init; }
    public double Seconds { get; init; }
    public string? FailureStage { get; init; }
    public string? KnownFault { get; init; }
    public string? Message { get; init; }

    /// <summary>构建产出的包，或运行所用的包。</summary>
    public string? Package { get; init; }
    public bool CacheHit { get; init; }
    public string? PatchName { get; init; }
    public string? RunName { get; init; }

    /// <summary>运行用的场景名，取自预置场景。</summary>
    public string? Scenario { get; init; }

    /// <summary>运行的类别：patched 母包加补丁，rebuild 整包重编的包，baseline 母包自身。</summary>
    public string? RunKind { get; init; }

    /// <summary>运行产出的逐 tick 分层哈希，绝对路径。</summary>
    public string? Csv { get; init; }

    public string? At { get; init; }
}

public static class LedgerSteps
{
    public const string BaselineBuild = "baseline_build";
    public const string RebuildBuild = "rebuild_build";
    public const string PatchCompile = "patch_compile";
    public const string Run = "run";

    public const string Patched = "patched";
    public const string Rebuild = "rebuild";
    public const string Baseline = "baseline";
}

/// <summary>
/// 一次 MCP 会话做过的每一步：服务端在每步完成时记下，验证脚本事后据此判定。
///
/// MCP 一路由 Agent 按顺序调用工具，判定却不能交给 Agent（裁决 009）。服务端只记事实，不下结论；
/// 结论由命令行的 judge 用与命令行验证同一套规则（<see cref="CaseVerdict"/>）从台账算出。
/// 每步完成即整份重写、先写临时文件再改名：服务端随时可能被客户端强杀（裁决 015），已完成的步骤不能丢，
/// 也不能留下半截的文件。
/// </summary>
public sealed class CaseLedger
{
    public const string FileName = "patchparity-ledger.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IReadOnlyList<string> _targets;
    private readonly List<LedgerEntry> _entries = new();
    private readonly object _gate = new();

    /// <param name="targets">台账写到哪些文件：题目工作树里那份给验证脚本读，会话产物目录里那份留档。</param>
    public CaseLedger(params string[] targets) => _targets = targets;

    public IReadOnlyList<LedgerEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Record(LedgerEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry with { At = entry.At ?? DateTime.UtcNow.ToString("O") });
            string text = JsonSerializer.Serialize(new LedgerFile { Version = 1, Entries = _entries }, Json);
            foreach (string target in _targets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                string partial = target + ".partial";
                File.WriteAllText(partial, text);
                File.Move(partial, target, overwrite: true);
            }
        }
    }

    public static IReadOnlyList<LedgerEntry> Load(string path)
    {
        LedgerFile file = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(path), Json)
                          ?? throw new InvalidDataException("台账为空：" + path);
        return file.Entries;
    }

    /// <summary>
    /// 从台账还原证据。按验证的含义挑出各步：最后一次母包基线构建、最后一次补丁编译、
    /// 在母包基线上装该补丁的前两次运行、最后一次整包重编、在该包上的第一次运行。
    /// 挑出的各步彼此对不上（运行用的不是这个母包、不是这个补丁、场景不同）时返回不一致的原因，不勉强判定。
    /// </summary>
    public static (CaseEvidence Evidence, string? Inconsistency) ToEvidence(
        IReadOnlyList<LedgerEntry> entries, IReadOnlyCollection<string> touchedComponents)
    {
        LedgerEntry? baseline = entries.LastOrDefault(e => e.Step == LedgerSteps.BaselineBuild);
        LedgerEntry? compile = entries.LastOrDefault(e => e.Step == LedgerSteps.PatchCompile);
        LedgerEntry[] patched = entries.Where(e => e.Step == LedgerSteps.Run && e.RunKind == LedgerSteps.Patched).Take(2).ToArray();
        LedgerEntry? rebuild = entries.LastOrDefault(e => e.Step == LedgerSteps.RebuildBuild);
        LedgerEntry? rebuildRun = rebuild?.Package is { } rebuildPackage
            ? entries.FirstOrDefault(e => e.Step == LedgerSteps.Run && e.RunKind == LedgerSteps.Rebuild && e.Package == rebuildPackage)
            : null;

        string? inconsistency = null;
        foreach (LedgerEntry run in patched)
        {
            if (baseline?.Package is { } baselinePackage && run.Package != baselinePackage)
            {
                inconsistency = $"补丁运行 {run.RunName} 用的包 {run.Package} 不是母包基线 {baselinePackage}";
            }
            else if (compile?.PatchName is { } patchName && run.PatchName != patchName)
            {
                inconsistency = $"补丁运行 {run.RunName} 装的补丁 {run.PatchName} 不是编译的 {patchName}";
            }
        }
        string[] scenarios = patched.Append(rebuildRun).OfType<LedgerEntry>().Select(e => e.Scenario ?? "").Distinct().ToArray();
        if (scenarios.Length > 1)
        {
            inconsistency = "各次运行的场景不同：" + string.Join("、", scenarios);
        }

        var evidence = new CaseEvidence
        {
            BaselineBuild = baseline is null ? null : Step(baseline),
            BaselineReady = baseline is { Ok: true },
            PatchCompile = compile is null ? null : Step(compile),
            PatchRun = patched.Length > 0 ? Run(patched[0]) : null,
            PatchRunAgain = patched.Length > 1 ? Run(patched[1]) : null,
            Rebuild = rebuild is null ? null : Step(rebuild),
            RebuildRun = rebuildRun is null ? null : Run(rebuildRun),
            TouchedComponents = touchedComponents,
        };
        return (evidence, inconsistency);
    }

    private static StepResult Step(LedgerEntry e) => new()
    {
        Step = e.Step,
        Succeeded = e.Ok,
        Elapsed = TimeSpan.FromSeconds(e.Seconds),
        Failure = Failure(e),
    };

    private static RunResult Run(LedgerEntry e) => new()
    {
        Step = e.RunName ?? e.Step,
        Succeeded = e.Ok && e.Csv is not null && File.Exists(e.Csv),
        Elapsed = TimeSpan.FromSeconds(e.Seconds),
        Failure = e.Ok && (e.Csv is null || !File.Exists(e.Csv))
            ? new FailureReport { Stage = FailureStage.Runtime, Message = "台账记为成功，但哈希文件不在：" + e.Csv }
            : Failure(e),
        CsvPath = e.Csv,
    };

    private static FailureReport Failure(LedgerEntry e) => e.Ok
        ? new FailureReport { Stage = FailureStage.None }
        : new FailureReport
        {
            Stage = Enum.TryParse(e.FailureStage, out FailureStage stage) ? stage : FailureStage.Runtime,
            Fault = Enum.TryParse(e.KnownFault, out KnownFault fault) ? fault : KnownFault.Unknown,
            Message = e.Message,
        };

    private sealed class LedgerFile
    {
        public int Version { get; init; }
        public List<LedgerEntry> Entries { get; init; } = new();
    }
}

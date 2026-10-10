using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PatchParity.Pipeline;

namespace PatchParity.Mcp;

/// <summary>工具声明：名字、说明、入参结构，以及是否会写盘。</summary>
public sealed record ToolSpec
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject InputSchema { get; init; }

    /// <summary>只读工具不产生副作用，风险档据此配置。</summary>
    public required bool ReadOnly { get; init; }
}

/// <summary>
/// 七个工具，粒度为一步一个。刻意不提供"一键验证"——编排由调用方按顺序组合，
/// 每一步的产物与耗时都单独可见，失败时也能看出停在哪一步。
///
/// 工具之间以显式标识衔接：包以内容哈希键标识，补丁以名字标识，运行以名字标识。
/// 不依赖"当前源码处于何种状态"的隐式约定——源码状态恰恰会被中间的施加改动这一步改掉。
///
/// 包与补丁分离：包是整包构建的产物，进缓存后不可变；补丁只是一个装着重编热更程序集的目录。
/// "母包加补丁"在运行时组合，不向包内写入任何东西。
///
/// 启动 Unity 的工具（build_package、build_patch、run_scenario）先同步校验入参，通过后在后台运行并立即返回任务号，
/// 由 wait_job 分次等待。MCP 客户端普遍对单次请求设 60 秒上限，而整包构建要十余分钟。
///
/// 会话之间的隔离：每个会话（每个服务端进程）有自己的产物目录，运行与补丁按名字只在本会话内可见；
/// 母包基线缓存跨会话共享。改动源码或 Unity 工程的工具（build_package、build_patch、revert_patch）
/// 须先取得工作区租约，被其他会话持有时明确拒绝、不排队。
/// </summary>
public sealed class Tools
{
    private readonly ToolPaths _paths;
    private readonly UnityInvoker _unity;
    private readonly IPatchSource _patch;
    private readonly BaselineCache _cache;
    private readonly Func<CacheKey> _packageKey;
    private readonly WorkspaceLease _lease;

    // 本会话施加过、尚未撤销的改动。会话结束时兜底撤销，避免把被测样例的源码留在改过的状态
    private readonly Dictionary<string, ChangeCase> _applied = new(StringComparer.Ordinal);

    private readonly Jobs _jobs = new();

    /// <summary>wait_job 单次等待的上限，须低于客户端的请求超时。</summary>
    public const int MaxWaitSeconds = 50;

    public Tools(
        ToolPaths paths, UnityInvoker unity, IPatchSource patch, BaselineCache cache, Func<CacheKey> packageKey,
        WorkspaceLease lease)
    {
        _paths = paths;
        _unity = unity;
        _patch = patch;
        _cache = cache;
        _packageKey = packageKey;
        _lease = lease;
    }

    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>尚未撤销的改动，供测试与会话收尾检查。</summary>
    public IReadOnlyCollection<string> AppliedPatches
    {
        get
        {
            lock (_applied)
            {
                return _applied.Keys.ToArray();
            }
        }
    }

    /// <summary>预置场景。场景是验证的输入，不由调用方随意拼装，便于结果可比。</summary>
    public static readonly IReadOnlyDictionary<string, Scenario> Scenarios = new Dictionary<string, Scenario>
    {
        ["default"] = new(),
        ["short"] = new() { Ticks = 120 },
        ["long"] = new() { Ticks = 3000 },
        ["four-players"] = new() { Players = 4 },
        ["seed-7"] = new() { Seed = 7 },
    };

    public IReadOnlyList<ToolSpec> Specs => new[]
    {
        new ToolSpec
        {
            Name = "list_scenarios",
            Description = "列出可用的验证场景及其参数。",
            ReadOnly = true,
            InputSchema = Schema(new JsonObject()),
        },
        new ToolSpec
        {
            Name = "build_package",
            Description =
                "对当前源码做一次整包构建，返回包标识（内容哈希键）。命中缓存时直接复用。" +
                "施加改动前调用得母包基线，施加改动后调用得整包重编。" +
                "后台运行：立即返回任务号，用 wait_job 取结果，未命中缓存时约需十余分钟。",
            ReadOnly = false,
            InputSchema = Schema(new JsonObject()),
        },
        new ToolSpec
        {
            Name = "build_patch",
            Description =
                "施加改动用例，只重编热更程序集，产物存为一个补丁。不做整包构建，不改动任何已构建的包。" +
                "改动在调用 revert_patch 前保持施加状态。后台运行：立即返回任务号，用 wait_job 取结果。",
            ReadOnly = false,
            InputSchema = Schema(new JsonObject
            {
                ["patch_path"] = Property("string", "改动用例的 patch 路径"),
                ["patch_name"] = Property("string", "补丁名，后续 run_scenario 以此引用"),
            }, "patch_path", "patch_name"),
        },
        new ToolSpec
        {
            Name = "revert_patch",
            Description = "撤销一个已施加的改动，使源码回到改动前的状态。未施加时无副作用。",
            ReadOnly = false,
            InputSchema = Schema(new JsonObject
            {
                ["patch_path"] = Property("string", "要撤销的改动用例的 patch 路径"),
            }, "patch_path"),
        },
        new ToolSpec
        {
            Name = "run_scenario",
            Description =
                "在指定的包上跑一个场景，产出逐 tick 分层哈希，并给出覆盖信号。" +
                "给出 patch_name 时从该补丁加载热更程序集，即\"母包加补丁\"；不给则运行包自身。" +
                "后台运行：立即返回任务号，用 wait_job 取结果。",
            ReadOnly = false,
            InputSchema = Schema(new JsonObject
            {
                ["package"] = Property("string", "包标识，取自 build_package 的返回"),
                ["patch_name"] = Property("string", "可选，补丁名，取自 build_patch"),
                ["scenario"] = Property("string", "场景名，取自 list_scenarios，缺省 default"),
                ["run_name"] = Property("string", "本次运行的名字，compare_runs 以此引用"),
            }, "package", "run_name"),
        },
        new ToolSpec
        {
            Name = "wait_job",
            Description =
                "等待一个后台任务，至多等待 max_wait_seconds 秒（上限 50）。" +
                "返回 running 时再次调用；返回 succeeded 或 failed 时附带该任务的完整结果与证据。",
            ReadOnly = true,
            InputSchema = Schema(new JsonObject
            {
                ["job_id"] = Property("string", "任务号，取自启动任务的工具的返回"),
                ["max_wait_seconds"] = Property("integer", "本次至多等待的秒数，缺省且上限为 50"),
            }, "job_id"),
        },
        new ToolSpec
        {
            Name = "compare_runs",
            Description =
                "比对两次运行的逐 tick 分层哈希，给出判定、首个分歧 tick 与分歧层，并附覆盖自检。" +
                "比对同一包同一补丁的两次运行即为确定性自检。",
            ReadOnly = true,
            InputSchema = Schema(new JsonObject
            {
                ["left"] = Property("string", "左侧运行的名字，通常是整包重编"),
                ["right"] = Property("string", "右侧运行的名字，通常是母包加补丁"),
                ["touched_components"] = Property("string", "改动触及的组件，逗号分隔，用于覆盖自检"),
            }, "left", "right"),
        },
    };

    public ToolResult Invoke(string name, JsonElement arguments) => name switch
    {
        "list_scenarios" => ListScenarios(),
        "build_package" => WithLease(() => StartJob("build_package", BuildPackage)),
        "build_patch" => BuildPatch(arguments),
        "revert_patch" => RevertPatch(arguments),
        "run_scenario" => RunScenario(arguments),
        "wait_job" => WaitJob(arguments),
        "compare_runs" => CompareRuns(arguments),
        _ => ToolResult.Failure("未知工具: " + name),
    };

    /// <summary>撤销本会话施加过的全部改动。会话结束时调用；单个撤销失败不影响其余。</summary>
    public IReadOnlyList<string> RevertAll()
    {
        // 编译任务还在读写源码时撤销改动会得到半截状态，故先等后台任务结束
        _jobs.WaitAll(BuildTimeout + CompileTimeout);

        var failures = new List<string>();
        List<ChangeCase> applied;
        lock (_applied)
        {
            applied = _applied.Values.ToList();
        }
        foreach (ChangeCase change in applied)
        {
            try
            {
                _patch.Revert(change);
                lock (_applied)
                {
                    _applied.Remove(change.PatchPath);
                }
            }
            catch (Exception e)
            {
                failures.Add(change.PatchPath + ": " + e.Message);
            }
        }
        return failures;
    }

    private ToolResult ListScenarios()
    {
        var list = new JsonArray();
        var text = new StringBuilder("可用场景：\n");
        foreach ((string key, Scenario scenario) in Scenarios)
        {
            list.Add(new JsonObject
            {
                ["name"] = key,
                ["seed"] = scenario.Seed,
                ["ticks"] = scenario.Ticks,
                ["players"] = scenario.Players,
                ["bots"] = scenario.Bots,
            });
            text.Append("  ").Append(key).Append("  ").Append(scenario.ToOptionString()).Append('\n');
        }

        return new ToolResult
        {
            Ok = true,
            Text = text.ToString(),
            Structured = new JsonObject { ["scenarios"] = list },
        };
    }

    private ToolResult BuildPackage()
    {
        CacheKey key = _packageKey();
        CacheLookup lookup = _cache.Lookup(key);

        if (lookup.Hit)
        {
            return new ToolResult
            {
                Ok = true,
                Text = $"命中缓存，跳过构建。包标识 {key.Value}\n{key.Describe()}",
                Structured = new JsonObject { ["package"] = key.Value, ["cache_hit"] = true },
                Evidence = key.Describe(),
            };
        }

        StepResult step = _unity.BuildPlayer(lookup.PackageDirectory, BuildTimeout, "build-" + key.Value);
        if (!step.Succeeded)
        {
            _cache.Invalidate(key);
            return new ToolResult
            {
                Ok = false,
                Text = "整包构建失败：" + Describe(step.Failure),
                Structured = new JsonObject
                {
                    ["cache_hit"] = false,
                    ["failure_stage"] = step.Failure.Stage.ToString(),
                    ["known_fault"] = step.Failure.Fault.ToString(),
                },
                Evidence = step.Report ?? step.Failure.Message,
            };
        }

        _cache.MarkComplete(key);
        return new ToolResult
        {
            Ok = true,
            Text = $"整包构建完成，耗时 {step.Elapsed.TotalSeconds:F1} 秒。包标识 {key.Value}\n{key.Describe()}",
            Structured = new JsonObject
            {
                ["package"] = key.Value,
                ["cache_hit"] = false,
                ["seconds"] = Math.Round(step.Elapsed.TotalSeconds, 1),
            },
            Evidence = key.Describe() + "\n" + step.Report,
        };
    }

    private ToolResult BuildPatch(JsonElement arguments)
    {
        if (!TryGetString(arguments, "patch_path", out string patchPath) ||
            !TryGetString(arguments, "patch_name", out string patchName))
        {
            return ToolResult.Failure("缺少 patch_path 或 patch_name");
        }
        if (!IsSafeName(patchName))
        {
            return ToolResult.Failure("补丁名只能含字母、数字、连字符与下划线: " + patchName);
        }

        var change = new ChangeCase
        {
            Name = patchName,
            PatchPath = patchPath,
            Expected = ExpectedVerdict.Boundary,
        };

        return WithLease(() => StartJob("build_patch", () => CompilePatch(change, patchName)));
    }

    private ToolResult CompilePatch(ChangeCase change, string patchName)
    {
        _patch.Apply(change);
        lock (_applied)
        {
            _applied[change.PatchPath] = change;
        }

        StepResult step = _unity.CompileHotUpdate(PatchDirectory(patchName), CompileTimeout, "patch-" + patchName);

        return step.Succeeded
            ? new ToolResult
            {
                Ok = true,
                Text = $"补丁 {patchName} 编译完成，耗时 {step.Elapsed.TotalSeconds:F1} 秒。改动保持施加状态，结束前请调用 revert_patch。",
                Structured = new JsonObject
                {
                    ["patch_name"] = patchName,
                    ["seconds"] = Math.Round(step.Elapsed.TotalSeconds, 1),
                },
                Evidence = step.Report,
            }
            : new ToolResult
            {
                Ok = false,
                Text = "补丁编译失败：" + Describe(step.Failure) + "。改动仍处于施加状态，请调用 revert_patch。",
                Structured = new JsonObject
                {
                    ["failure_stage"] = step.Failure.Stage.ToString(),
                    ["known_fault"] = step.Failure.Fault.ToString(),
                },
                Evidence = step.Report ?? step.Failure.Message,
            };
    }

    private ToolResult RevertPatch(JsonElement arguments)
    {
        if (!TryGetString(arguments, "patch_path", out string patchPath))
        {
            return ToolResult.Failure("缺少 patch_path");
        }

        // 撤销也要租约：否则本会话可能把另一个会话施加的同一份改动撤掉
        return WithLease(() => Revert(patchPath));
    }

    private ToolResult Revert(string patchPath)
    {
        if (_jobs.RunningExclusive is { } running)
        {
            return ToolResult.Failure($"任务 {running} 仍在运行，此时撤销改动会得到半截状态。请先用 wait_job 等它结束。");
        }

        ChangeCase change;
        lock (_applied)
        {
            change = _applied.TryGetValue(patchPath, out ChangeCase? known)
                ? known
                : new ChangeCase { Name = "revert", PatchPath = patchPath, Expected = ExpectedVerdict.Boundary };
        }

        _patch.Revert(change);
        lock (_applied)
        {
            _applied.Remove(patchPath);
        }

        return new ToolResult
        {
            Ok = true,
            Text = "改动已撤销，源码回到改动前的状态。",
            Structured = new JsonObject { ["reverted"] = patchPath },
        };
    }

    private ToolResult RunScenario(JsonElement arguments)
    {
        if (!TryGetString(arguments, "package", out string package) ||
            !TryGetString(arguments, "run_name", out string runName))
        {
            return ToolResult.Failure("缺少 package 或 run_name");
        }
        if (!IsSafeName(runName))
        {
            return ToolResult.Failure("运行名只能含字母、数字、连字符与下划线: " + runName);
        }

        string scenarioName = TryGetString(arguments, "scenario", out string s) ? s : "default";
        if (!Scenarios.TryGetValue(scenarioName, out Scenario? scenario))
        {
            return ToolResult.Failure($"未知场景 {scenarioName}，可用场景见 list_scenarios");
        }

        string packageDir;
        try
        {
            if (!_cache.IsComplete(package))
            {
                return ToolResult.Failure($"包 {package} 不存在或未构建完成，请先调用 build_package");
            }
            packageDir = _cache.DirectoryFor(package);
        }
        catch (ArgumentException e)
        {
            return ToolResult.Failure(e.Message);
        }

        string? hotUpdateDir = null;
        if (TryGetString(arguments, "patch_name", out string patchName))
        {
            if (!IsSafeName(patchName))
            {
                return ToolResult.Failure("补丁名只能含字母、数字、连字符与下划线: " + patchName);
            }
            hotUpdateDir = Path.Combine(PatchDirectory(patchName), "HotUpdateDlls");
            if (!Directory.Exists(hotUpdateDir))
            {
                return ToolResult.Failure($"补丁 {patchName} 不存在，请先调用 build_patch");
            }
        }

        string capturedPatch = hotUpdateDir is null ? "" : patchName;
        return StartJob("run_scenario",
            () => ExecuteRun(package, packageDir, runName, scenarioName, scenario, capturedPatch, hotUpdateDir));
    }

    private ToolResult ExecuteRun(
        string package, string packageDir, string runName, string scenarioName, Scenario scenario,
        string patchName, string? hotUpdateDir)
    {
        RunResult run = _unity.RunScenario(
            packageDir, RunDirectory(runName), scenario, RunTimeout, "run-" + runName, hotUpdateDir);

        if (!run.Succeeded)
        {
            return new ToolResult
            {
                Ok = false,
                Text = "运行失败：" + Describe(run.Failure),
                Structured = new JsonObject
                {
                    ["run_name"] = runName,
                    ["failure_stage"] = run.Failure.Stage.ToString(),
                    ["known_fault"] = run.Failure.Fault.ToString(),
                },
                Evidence = run.Report,
            };
        }

        HashSeries series = HashSeries.Load(run.CsvPath!);
        int live = series.LiveComponents(series.Count - 1);

        return new ToolResult
        {
            Ok = true,
            Text = $"运行 {runName} 完成，{series.Count} 帧，末帧组件实例数 {live}，耗时 {run.Elapsed.TotalSeconds:F1} 秒。",
            Structured = new JsonObject
            {
                ["run_name"] = runName,
                ["package"] = package,
                ["patch_name"] = hotUpdateDir is null ? null : patchName,
                ["scenario"] = scenarioName,
                ["ticks"] = series.Count,
                ["live_components"] = live,
                ["seconds"] = Math.Round(run.Elapsed.TotalSeconds, 1),
            },
            Evidence = run.Report,
        };
    }

    private ToolResult WithLease(Func<ToolResult> action)
    {
        if (!_lease.TryAcquire(out string? holder))
        {
            return ToolResult.Failure(
                $"工作区被另一个会话占用（{holder}）。同一个 Unity 工程同一时刻只允许一个会话施加改动或构建，" +
                "本次调用被拒绝，不排队。");
        }
        return action();
    }

    private ToolResult StartJob(string tool, Func<ToolResult> work)
    {
        string? id = _jobs.TryStartExclusive(tool, work, out string? blockedBy);
        if (id is null)
        {
            return ToolResult.Failure(
                $"任务 {blockedBy} 仍在运行，同一时刻只允许一个会改动工程的任务。请先用 wait_job 等它结束。");
        }

        return new ToolResult
        {
            Ok = true,
            Text = $"任务 {id} 已在后台启动。请调用 wait_job（job_id={id}）取结果，" +
                   $"每次至多等待 {MaxWaitSeconds} 秒，返回 running 就再次调用。",
            Structured = new JsonObject { ["job_id"] = id, ["status"] = "running", ["tool"] = tool },
        };
    }

    private ToolResult WaitJob(JsonElement arguments)
    {
        if (!TryGetString(arguments, "job_id", out string id))
        {
            return ToolResult.Failure("缺少 job_id");
        }

        int seconds = MaxWaitSeconds;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("max_wait_seconds", out JsonElement waitElement) &&
            waitElement.ValueKind == JsonValueKind.Number &&
            waitElement.TryGetInt32(out int requested))
        {
            seconds = Math.Clamp(requested, 1, MaxWaitSeconds);
        }

        JobSnapshot? snapshot = _jobs.Wait(id, TimeSpan.FromSeconds(seconds));
        if (snapshot is null)
        {
            return ToolResult.Failure("未知任务号: " + id);
        }

        double elapsed = Math.Round(snapshot.Elapsed.TotalSeconds, 1);
        if (!snapshot.Completed)
        {
            return new ToolResult
            {
                Ok = true,
                Text = $"任务 {id} 仍在运行，已用 {elapsed} 秒。请再次调用 wait_job。",
                Structured = new JsonObject
                {
                    ["job_id"] = id,
                    ["tool"] = snapshot.Tool,
                    ["status"] = "running",
                    ["elapsed_seconds"] = elapsed,
                },
            };
        }

        // 完成：原样交回该任务的结果与证据，附上任务信息
        ToolResult result = snapshot.Result!;
        var structured = result.Structured is null ? new JsonObject() : (JsonObject)result.Structured.DeepClone();
        structured["job_id"] = id;
        structured["tool"] = snapshot.Tool;
        structured["status"] = result.Ok ? "succeeded" : "failed";
        structured["elapsed_seconds"] = elapsed;

        return result with
        {
            Text = $"任务 {id}（{snapshot.Tool}）已完成，用时 {elapsed} 秒。\n" + result.Text,
            Structured = structured,
        };
    }

    private ToolResult CompareRuns(JsonElement arguments)
    {
        if (!TryGetString(arguments, "left", out string left) || !TryGetString(arguments, "right", out string right))
        {
            return ToolResult.Failure("缺少 left 或 right");
        }
        if (!IsSafeName(left) || !IsSafeName(right))
        {
            return ToolResult.Failure("运行名只能含字母、数字、连字符与下划线");
        }

        string leftCsv = Path.Combine(RunDirectory(left), "hashes.csv");
        string rightCsv = Path.Combine(RunDirectory(right), "hashes.csv");
        if (!File.Exists(leftCsv) || !File.Exists(rightCsv))
        {
            return ToolResult.Failure("找不到运行产物，请先调用 run_scenario");
        }

        string[] touched = TryGetString(arguments, "touched_components", out string t)
            ? t.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        HashSeries leftSeries = HashSeries.Load(leftCsv);
        HashSeries rightSeries = HashSeries.Load(rightCsv);
        CoverageCheck coverage = Preconditions.CheckCoverage(rightSeries, touched);
        ComparisonResult comparison = SeriesComparer.Compare(leftSeries, rightSeries);

        // 覆盖不过时不得报"一致"：那种一致可能只说明改动点没被执行
        string verdict = comparison.Identical
            ? (coverage.Passed ? "equivalent" : "void")
            : "divergent";

        var evidence = new StringBuilder();
        evidence.Append("判定 ").Append(verdict).Append('\n');
        evidence.Append("左 ").Append(left).Append(' ').Append(comparison.LeftCount).Append(" 帧\n");
        evidence.Append("右 ").Append(right).Append(' ').Append(comparison.RightCount).Append(" 帧\n");
        evidence.Append("覆盖自检 ").Append(coverage.Passed ? "通过" : "不通过")
                .Append(" 末帧组件实例数=").Append(coverage.LiveComponents).Append('\n');
        if (comparison.FirstDivergentTick is int tick)
        {
            evidence.Append("首个分歧 tick=").Append(tick.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (LayerDifference layer in comparison.DivergentLayers)
            {
                evidence.Append("  ").Append(layer.Layer)
                        .Append(" 左=").Append(layer.Left)
                        .Append(" 右=").Append(layer.Right).Append('\n');
            }
        }

        var layers = new JsonArray();
        foreach (LayerDifference layer in comparison.DivergentLayers)
        {
            layers.Add(new JsonObject { ["layer"] = layer.Layer, ["left"] = layer.Left, ["right"] = layer.Right });
        }

        return new ToolResult
        {
            Ok = verdict != "divergent",
            Text = evidence.ToString(),
            Structured = new JsonObject
            {
                ["verdict"] = verdict,
                ["identical"] = comparison.Identical,
                ["first_divergent_tick"] = comparison.FirstDivergentTick,
                ["divergent_layers"] = layers,
                ["coverage_passed"] = coverage.Passed,
                ["live_components"] = coverage.LiveComponents,
                ["diverged_outside_layers"] = comparison.DivergedOutsideLayers,
            },
            Evidence = evidence.ToString(),
        };
    }

    private string PatchDirectory(string patchName) => Path.Combine(_paths.ArtifactRoot, "patches", patchName);

    private string RunDirectory(string runName) => Path.Combine(_paths.ArtifactRoot, "runs", runName);

    // 名字会拼进路径，只放行安全字符，杜绝经由名字跳出产物目录
    private static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 64 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Describe(FailureReport failure)
    {
        string stage = failure.Stage switch
        {
            FailureStage.Build => "构建期",
            FailureStage.Load => "加载期",
            FailureStage.Runtime => "运行期",
            FailureStage.Mismatch => "哈希分歧",
            _ => "未知阶段",
        };
        return failure.Fault == KnownFault.Unknown
            ? $"{stage}，{failure.Message}"
            : $"{stage}，已知故障 {failure.Fault}，{failure.Message}";
    }

    private static bool TryGetString(JsonElement arguments, string name, out string value)
    {
        value = "";
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString() ?? "";
        return value.Length > 0;
    }

    private static JsonObject Property(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Length > 0)
        {
            var array = new JsonArray();
            foreach (string name in required)
            {
                array.Add(name);
            }
            schema["required"] = array;
        }
        return schema;
    }
}

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
/// 五个工具，粒度为一步一个：单独构建基线、单独打补丁、单独跑场景、单独比对、列出场景。
/// 刻意不提供"一键验证"——编排由调用方按顺序组合，每一步的产物与耗时都单独可见，
/// 失败时也能看出停在哪一步。
/// </summary>
public sealed class Tools
{
    private readonly ToolPaths _paths;
    private readonly UnityInvoker _unity;
    private readonly IPatchSource _patch;
    private readonly BaselineCache _cache;
    private readonly Func<CacheKey> _baselineKey;

    public Tools(ToolPaths paths, UnityInvoker unity, IPatchSource patch, BaselineCache cache, Func<CacheKey> baselineKey)
    {
        _paths = paths;
        _unity = unity;
        _patch = patch;
        _cache = cache;
        _baselineKey = baselineKey;
    }

    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(10);

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
            Name = "build_baseline",
            Description = "构建母包基线（改动前的源码）。命中内容哈希缓存时直接复用，返回缓存键与其组成。",
            ReadOnly = false,
            InputSchema = Schema(new JsonObject()),
        },
        new ToolSpec
        {
            Name = "build_patch",
            Description = "施加改动用例并只重编热更程序集，替换进母包基线。不做整包重编。",
            ReadOnly = false,
            InputSchema = Schema(new JsonObject
            {
                ["patch_path"] = Property("string", "改动用例的 patch 路径"),
            }, "patch_path"),
        },
        new ToolSpec
        {
            Name = "run_scenario",
            Description = "在指定包上跑一个场景，产出逐 tick 分层哈希，并给出覆盖信号。",
            ReadOnly = false,
            InputSchema = Schema(new JsonObject
            {
                ["scenario"] = Property("string", "场景名，取自 list_scenarios"),
                ["package"] = Property("string", "baseline 或 rebuild"),
                ["run_name"] = Property("string", "本次运行的名字，决定产物目录"),
            }, "scenario", "run_name"),
        },
        new ToolSpec
        {
            Name = "compare_runs",
            Description = "比对两次运行的逐 tick 分层哈希，给出判定、首个分歧 tick 与分歧层，并附覆盖自检。",
            ReadOnly = true,
            InputSchema = Schema(new JsonObject
            {
                ["left"] = Property("string", "左侧运行的名字，通常是整包重编"),
                ["right"] = Property("string", "右侧运行的名字，通常是打补丁"),
                ["touched_components"] = Property("string", "改动触及的组件，逗号分隔，用于覆盖自检"),
            }, "left", "right"),
        },
    };

    public ToolResult Invoke(string name, JsonElement arguments) => name switch
    {
        "list_scenarios" => ListScenarios(),
        "build_baseline" => BuildBaseline(),
        "build_patch" => BuildPatch(arguments),
        "run_scenario" => RunScenario(arguments),
        "compare_runs" => CompareRuns(arguments),
        _ => ToolResult.Failure("未知工具: " + name),
    };

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

    private ToolResult BuildBaseline()
    {
        CacheKey key = _baselineKey();
        CacheLookup lookup = _cache.Lookup(key);

        if (lookup.Hit)
        {
            return new ToolResult
            {
                Ok = true,
                Text = $"命中母包基线缓存，跳过构建。\n{key.Describe()}",
                Structured = new JsonObject
                {
                    ["cache_hit"] = true,
                    ["package"] = lookup.PackageDirectory,
                    ["cache_key"] = key.Value,
                },
                Evidence = key.Describe(),
            };
        }

        StepResult step = _unity.BuildPlayer(lookup.PackageDirectory, BuildTimeout, "baseline-build");
        if (!step.Succeeded)
        {
            _cache.Invalidate(key);
            return new ToolResult
            {
                Ok = false,
                Text = "母包基线构建失败：" + Describe(step.Failure),
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
            Text = $"母包基线构建完成，耗时 {step.Elapsed.TotalSeconds:F1} 秒。\n{key.Describe()}",
            Structured = new JsonObject
            {
                ["cache_hit"] = false,
                ["package"] = lookup.PackageDirectory,
                ["cache_key"] = key.Value,
                ["seconds"] = Math.Round(step.Elapsed.TotalSeconds, 1),
            },
            Evidence = key.Describe() + "\n" + step.Report,
        };
    }

    private ToolResult BuildPatch(JsonElement arguments)
    {
        if (!TryGetString(arguments, "patch_path", out string patchPath))
        {
            return ToolResult.Failure("缺少 patch_path");
        }

        var change = new ChangeCase
        {
            Name = Path.GetFileNameWithoutExtension(patchPath),
            PatchPath = patchPath,
            Expected = ExpectedVerdict.Boundary,
        };

        CacheLookup lookup = _cache.Lookup(_baselineKey());
        if (!lookup.Hit)
        {
            return ToolResult.Failure("母包基线尚未构建，请先调用 build_baseline");
        }

        _patch.Apply(change);
        StepResult step = _unity.CompileHotUpdate(lookup.PackageDirectory, CompileTimeout, "patch-compile");

        return step.Succeeded
            ? new ToolResult
            {
                Ok = true,
                Text = $"补丁编译完成并已换进母包基线，耗时 {step.Elapsed.TotalSeconds:F1} 秒。",
                Structured = new JsonObject
                {
                    ["package"] = lookup.PackageDirectory,
                    ["seconds"] = Math.Round(step.Elapsed.TotalSeconds, 1),
                },
                Evidence = step.Report,
            }
            : new ToolResult
            {
                Ok = false,
                Text = "补丁编译失败：" + Describe(step.Failure),
                Structured = new JsonObject
                {
                    ["failure_stage"] = step.Failure.Stage.ToString(),
                    ["known_fault"] = step.Failure.Fault.ToString(),
                },
                Evidence = step.Report ?? step.Failure.Message,
            };
    }

    private ToolResult RunScenario(JsonElement arguments)
    {
        if (!TryGetString(arguments, "run_name", out string runName))
        {
            return ToolResult.Failure("缺少 run_name");
        }
        string scenarioName = TryGetString(arguments, "scenario", out string s) ? s : "default";
        if (!Scenarios.TryGetValue(scenarioName, out Scenario? scenario))
        {
            return ToolResult.Failure($"未知场景 {scenarioName}，可用场景见 list_scenarios");
        }

        string packageKind = TryGetString(arguments, "package", out string p) ? p : "baseline";
        string packageDir = packageKind == "rebuild"
            ? Path.Combine(_paths.ArtifactRoot, "rebuild-package")
            : _cache.Lookup(_baselineKey()).PackageDirectory;

        RunResult run = _unity.RunScenario(
            packageDir, Path.Combine(_paths.ArtifactRoot, "run-" + runName), scenario, RunTimeout, runName);

        if (!run.Succeeded)
        {
            return new ToolResult
            {
                Ok = false,
                Text = "运行失败：" + Describe(run.Failure),
                Structured = new JsonObject
                {
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
            Text = $"运行完成，{series.Count} 帧，末帧组件实例数 {live}，耗时 {run.Elapsed.TotalSeconds:F1} 秒。",
            Structured = new JsonObject
            {
                ["run_name"] = runName,
                ["scenario"] = scenarioName,
                ["ticks"] = series.Count,
                ["live_components"] = live,
                ["csv"] = run.CsvPath,
                ["seconds"] = Math.Round(run.Elapsed.TotalSeconds, 1),
            },
            Evidence = run.Report,
        };
    }

    private ToolResult CompareRuns(JsonElement arguments)
    {
        if (!TryGetString(arguments, "left", out string left) || !TryGetString(arguments, "right", out string right))
        {
            return ToolResult.Failure("缺少 left 或 right");
        }

        string leftCsv = Path.Combine(_paths.ArtifactRoot, "run-" + left, "hashes.csv");
        string rightCsv = Path.Combine(_paths.ArtifactRoot, "run-" + right, "hashes.csv");
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

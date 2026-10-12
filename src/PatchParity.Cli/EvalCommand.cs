using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PatchParity.Cli;

/// <summary>
/// 跑一批题：每道题一个干净的工作目录，交给 Agent 经 patchparity 的 MCP 工具做完，再跑题目自带的验证脚本判分。
///
/// 取代 Pigeon 65ff318 的 pigeon eval（之后的版本移除了这个跑题器，裁决 020）。题目仍由 eval/generate-tasks.mjs 生成，
/// 判分仍是题目里的 verify.mjs 调 patchparity judge；这里只做中间那一层：建目录、起 Agent、跑验证脚本、记账。
///
/// - 每道题自带一套 Pigeon 治理根（MCP 配置、风险档、会话存档都在题目的工作目录里），并关掉会话检索：
///   题与题之间看不到彼此的会话（S6 实测过跨题检索，裁决 019）。
/// - Agent 的说法不进判分（裁决 009）；它调用过哪些工具从会话事件里统计，非 patchparity 的工具单列出来。
/// - 已有结果的题跳过：跑批中断后同一输出目录重跑即续跑。
/// </summary>
public static class EvalCommand
{
    // Agent 超出墙钟预算后再宽限多久才强行结束：Pigeon 自己按预算收尾，这里只兜底
    private static readonly TimeSpan AgentGrace = TimeSpan.FromMinutes(10);

    private const string ToolPrefix = "mcp__patchparity__";

    // 风险档 A（裁决 015）：list_scenarios 为 read，其余为 write，配合 --yolo；wait_job 与 compare_runs 记作 write，证据才进回执
    private static readonly string[] WriteTools =
        { "build_package", "build_patch", "revert_patch", "run_scenario", "wait_job", "compare_runs" };

    private static readonly JsonSerializerOptions Json = new()
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };

    // 退出码：0 全部判分通过 / 1 有题不通过 / 2 用法错误
    public static int Run(IReadOnlyList<string> args) => Run(args, new SystemProcessRunner(), Console.Out);

    public static int Run(IReadOnlyList<string> args, IProcessRunner runner, TextWriter log)
    {
        Options? options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("""
                用法: patchparity eval --tasks <题目目录> --out <输出目录> --mcp-config <.mcp.json> --pigeon <Pigeon 目录> --stream-fn <模块>
                                        [--provider deepseek] [--model deepseek-v4-flash] [--thinking off|...] [--node node] [--only <id 前缀>]
                """);
            return 2;
        }

        List<EvalTask> tasks = EvalTask.LoadAll(options.Tasks)
            .Where(t => t.Id.StartsWith(options.Only, StringComparison.Ordinal))
            .ToList();
        if (tasks.Count == 0)
        {
            Console.Error.WriteLine($"{options.Tasks} 下没有 id 以 '{options.Only}' 开头的题");
            return 2;
        }

        Directory.CreateDirectory(options.Out);
        var results = new List<JsonObject>();
        foreach (EvalTask task in tasks)
        {
            string workspace = Path.Combine(options.Out, task.Id);
            string resultPath = Path.Combine(workspace, "result.json");
            if (File.Exists(resultPath))
            {
                log.WriteLine($"==> {task.Id}：已有结果，跳过");
                results.Add(JsonNode.Parse(File.ReadAllText(resultPath))!.AsObject());
                continue;
            }

            log.WriteLine($"==> {task.Id}");
            JsonObject result = RunTask(task, workspace, options, runner);
            File.WriteAllText(resultPath, result.ToJsonString(JsonIndented));
            log.WriteLine($"  判分 {result["grade"]}｜判定 {result["judge"]?["verdict"]}｜" +
                          $"Agent {result["agent"]!["status"]}，{result["agent"]!["turns"]} 轮");
            results.Add(result);
        }

        WriteSummary(options.Out, results, log);
        return results.All(r => (string?)r["grade"] == "pass") ? 0 : 1;
    }

    private static JsonObject RunTask(EvalTask task, string workspace, Options options, IProcessRunner runner)
    {
        // 没有结果的目录是上次中途退出的残局：整个重来，不在半截状态上续
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
        Directory.CreateDirectory(Path.Combine(workspace, ".pigeon"));
        File.Copy(options.McpConfig, Path.Combine(workspace, ".mcp.json"));
        File.WriteAllText(Path.Combine(workspace, ".pigeon", "settings.json"), TierSettings().ToJsonString(JsonIndented));

        var started = DateTime.UtcNow;
        ProcessOutcome agent = runner.Run(new ProcessSpec(
            options.Node, AgentArguments(task, workspace, options), workspace, task.Prompt,
            TimeSpan.FromMilliseconds(task.WallClockMs) + AgentGrace,
            Path.Combine(workspace, "agent.out.txt"), Path.Combine(workspace, "agent.err.txt")));
        JsonObject? agentResult = LastJsonLine(agent.Stdout);

        // Agent 失败、超时也照跑验证脚本：判分只看服务端台账，台账里有什么就判什么
        string[] verifier = task.Verifier.Select(part => part.Replace("{TASK_DIR}", task.Directory, StringComparison.Ordinal)).ToArray();
        ProcessOutcome verify = runner.Run(new ProcessSpec(
            verifier[0], verifier[1..], workspace, null, TimeSpan.FromMilliseconds(task.VerifierTimeoutMs),
            Path.Combine(workspace, "verify.out.txt"), Path.Combine(workspace, "verify.err.txt")));
        bool pass = !verify.TimedOut && verify.ExitCode == 0;

        Dictionary<string, int> tools = CountTools(workspace, (string?)agentResult?["sessionId"]);
        var toolsNode = new JsonObject();
        foreach ((string name, int count) in tools.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            toolsNode[name] = count;
        }
        var foreign = new JsonArray();
        foreach (string name in tools.Keys.Where(n => !n.StartsWith(ToolPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            foreign.Add((JsonNode)name);
        }

        return new JsonObject
        {
            ["task"] = task.Id,
            ["grade"] = pass ? "pass" : "fail",
            ["verifier_exit"] = verify.TimedOut ? null : verify.ExitCode,
            ["judge"] = LastJsonLine(verify.Stdout),
            ["agent"] = new JsonObject
            {
                ["exit"] = agent.TimedOut ? null : agent.ExitCode,
                ["timed_out"] = agent.TimedOut,
                ["status"] = (string?)agentResult?["status"] ?? (agent.TimedOut ? "killed" : "no_result"),
                ["session"] = (string?)agentResult?["sessionId"],
                ["failure"] = agentResult?["failure"]?.DeepClone(),
                ["turns"] = agentResult?["turns"]?.DeepClone(),
                ["tool_calls"] = agentResult?["toolCalls"]?.DeepClone(),
                ["tokens"] = agentResult?["usage"]?["totalTokens"]?.DeepClone(),
                ["cost"] = agentResult?["usage"]?["cost"]?["total"]?.DeepClone(),
                ["error"] = agentResult?["errorMessage"]?.DeepClone(),
            },
            ["tools"] = toolsNode,
            ["foreign_tools"] = foreign,
            ["wall_seconds"] = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
        };
    }

    /// <summary>
    /// 起 Agent 的命令行。各开关的取舍：会话检索、派 worker、联网、推送记忆、钩子一律关掉——评测要的是 Agent 只凭题面与
    /// patchparity 的工具做题；--trust-config 只对本次放行 .mcp.json 里的服务端启动定义（无人值守时不放行即开跑前报错）。
    /// </summary>
    public static IReadOnlyList<string> AgentArguments(EvalTask task, string workspace, Options options)
    {
        var args = new List<string>
        {
            Path.Combine(options.Pigeon, "src", "cli", "index.ts"), "run",
            "--root", workspace,
            "--stream-fn", options.StreamFn,
            "--provider", options.Provider,
            "--model", options.Model,
            "--yolo", "--trust-config",
            "--no-session-search", "--no-spawn-workers", "--no-web", "--no-pushed-memory", "--no-hooks",
            "--max-turns", task.MaxTurns.ToString(CultureInfo.InvariantCulture),
            "--wall-clock", task.WallClockMs.ToString(CultureInfo.InvariantCulture),
            "--json",
        };
        // 推理档位：不给时 Pigeon 按模型信息定，5e70ca2 对 deepseek-v4-flash 取 high，而 65ff318 跑 S5、S6 时是关的。
        // 档位改变 Agent 的行为与花费，要与既往结果比较就得显式给出（云上实测，裁决 020）
        if (options.Thinking != "")
        {
            args.Add("--thinking");
            args.Add(options.Thinking);
        }
        return args;
    }

    /// <summary>Pigeon 设置里 mcp 一节：patchparity 七个工具的风险档。</summary>
    public static JsonObject TierSettings()
    {
        var tools = new JsonObject { ["list_scenarios"] = new JsonObject { ["tier"] = "read" } };
        foreach (string tool in WriteTools)
        {
            tools[tool] = new JsonObject { ["tier"] = "write" };
        }
        return new JsonObject
        {
            ["mcp"] = new JsonObject
            {
                ["servers"] = new JsonObject
                {
                    ["patchparity"] = new JsonObject { ["defaultTier"] = "write", ["tools"] = tools },
                },
            },
        };
    }

    /// <summary>
    /// 按工具名数 Agent 发起的工具调用。Pigeon 5e70ca2 的会话存档在 .pigeon/state/sessions/&lt;按工作目录编码的子目录&gt;/
    /// &lt;时间&gt;_&lt;会话号&gt;.jsonl，每行一个条目；调用记在消息条目的 toolCall 块里（连同入参），工具返回另记一条，不重复计。
    /// </summary>
    private static Dictionary<string, int> CountTools(string workspace, string? sessionId)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string sessions = Path.Combine(workspace, ".pigeon", "state", "sessions");
        if (!Directory.Exists(sessions))
        {
            return counts;
        }
        IEnumerable<string> files = Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories)
            .Where(f => sessionId is null || Path.GetFileNameWithoutExtension(f).EndsWith("_" + sessionId, StringComparison.Ordinal));
        foreach (string file in files)
        {
            foreach (string line in File.ReadLines(file))
            {
                if (!line.Contains("\"toolCall\"", StringComparison.Ordinal) ||
                    JsonNode.Parse(line) is not JsonObject e ||
                    e["message"]?["content"] is not JsonArray content)
                {
                    continue;
                }
                foreach (JsonNode? block in content)
                {
                    if ((string?)block?["type"] == "toolCall" && (string?)block["name"] is { } name)
                    {
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }
                }
            }
        }
        return counts;
    }

    /// <summary>Pigeon 的 --json 与 judge 都把结构化结果写在标准输出的最后一行。</summary>
    public static JsonObject? LastJsonLine(string output)
    {
        string? line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line is null)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteSummary(string outDir, List<JsonObject> results, TextWriter log)
    {
        File.WriteAllLines(Path.Combine(outDir, "results.jsonl"), results.Select(r => r.ToJsonString(Json)), new UTF8Encoding(false));

        var csv = new StringBuilder("task,grade,verdict,observable,first_divergent_tick,scenario,agent_status,turns,tool_calls,tokens,cost,minutes,foreign_tools\n");
        foreach (JsonObject r in results)
        {
            JsonNode? judge = r["judge"];
            JsonNode agent = r["agent"]!;
            string[] cells =
            {
                (string)r["task"]!, (string)r["grade"]!, Text(judge?["verdict"]), Text(judge?["observable"]),
                Text(judge?["first_divergent_tick"]), Text(judge?["scenario"]), Text(agent["status"]), Text(agent["turns"]),
                Text(agent["tool_calls"]), Text(agent["tokens"]), Text(agent["cost"]),
                ((double)r["wall_seconds"]! / 60).ToString("F1", CultureInfo.InvariantCulture),
                string.Join(' ', r["foreign_tools"]!.AsArray().Select(n => (string)n!)),
            };
            csv.AppendLine(string.Join(',', cells.Select(Csv)));
        }
        File.WriteAllText(Path.Combine(outDir, "summary.csv"), csv.ToString(), new UTF8Encoding(true));

        int passed = results.Count(r => (string?)r["grade"] == "pass");
        log.WriteLine($"判分通过 {passed} / {results.Count}，汇总 {Path.Combine(outDir, "summary.csv")}");
        foreach (JsonObject r in results.Where(r => r["foreign_tools"]!.AsArray().Count > 0))
        {
            log.WriteLine($"  注意：{r["task"]} 调用了非 patchparity 的工具 {string.Join("、", r["foreign_tools"]!.AsArray().Select(n => (string)n!))}");
        }
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue(out string? s) => s ?? "",
        JsonValue v when v.TryGetValue(out bool b) => b ? "true" : "false",
        _ => node.ToJsonString(Json),
    };

    private static string Csv(string cell) =>
        cell.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;

    public sealed class Options
    {
        public string Tasks = "";
        public string Out = "";
        public string McpConfig = "";
        public string Pigeon = "";
        public string StreamFn = "";
        public string Provider = "deepseek";
        public string Model = "deepseek-v4-flash";
        public string Thinking = "";
        public string Node = "node";
        public string Only = "";

        public static Options? Parse(IReadOnlyList<string> args)
        {
            var o = new Options();
            for (int i = 1; i < args.Count; i++)
            {
                if (i + 1 >= args.Count)
                {
                    return null;
                }
                string value = args[++i];
                switch (args[i - 1])
                {
                    case "--tasks": o.Tasks = value; break;
                    case "--out": o.Out = value; break;
                    case "--mcp-config": o.McpConfig = value; break;
                    case "--pigeon": o.Pigeon = value; break;
                    case "--stream-fn": o.StreamFn = value; break;
                    case "--provider": o.Provider = value; break;
                    case "--model": o.Model = value; break;
                    case "--thinking": o.Thinking = value; break;
                    case "--node": o.Node = value; break;
                    case "--only": o.Only = value; break;
                    default: return null;
                }
            }
            bool complete = o.Tasks != "" && o.Out != "" && o.McpConfig != "" && o.Pigeon != "" && o.StreamFn != "";
            return complete && File.Exists(o.McpConfig) && Directory.Exists(o.Tasks) ? o : null;
        }
    }
}

/// <summary>eval/generate-tasks.mjs 生成的一道题：task.json、题面与验证脚本。</summary>
public sealed record EvalTask(
    string Id, string Directory, string Prompt, int MaxTurns, long WallClockMs,
    IReadOnlyList<string> Verifier, long VerifierTimeoutMs)
{
    public static IEnumerable<EvalTask> LoadAll(string tasksDir) =>
        System.IO.Directory.EnumerateDirectories(tasksDir)
            .Where(d => File.Exists(Path.Combine(d, "task.json")))
            .Order(StringComparer.Ordinal)
            .Select(Load);

    public static EvalTask Load(string dir)
    {
        JsonObject task = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "task.json")))!.AsObject();
        string id = (string?)task["id"] ?? throw new InvalidDataException("task.json 缺 id：" + dir);
        string prompt = File.ReadAllText(Path.Combine(dir, (string?)task["instructions"] ?? "task.md"));
        JsonNode budget = task["budget"] ?? throw new InvalidDataException("task.json 缺 budget：" + dir);
        JsonNode verifier = task["verifier"] ?? throw new InvalidDataException("task.json 缺 verifier：" + dir);
        string[] command = verifier["command"]!.AsArray().Select(n => (string)n!).ToArray();
        if (command.Length == 0)
        {
            throw new InvalidDataException("task.json 的 verifier.command 为空：" + dir);
        }
        return new EvalTask(
            id, Path.GetFullPath(dir), prompt,
            (int)budget["maxTurns"]!, (long)budget["wallClockMs"]!,
            command, (long?)verifier["timeoutMs"] ?? 120_000);
    }
}

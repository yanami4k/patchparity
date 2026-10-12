using System.Text.Json.Nodes;
using PatchParity.Cli;
using Xunit;

namespace PatchParity.Cli.Tests;

/// <summary>
/// eval 子命令：取代 Pigeon 65ff318 的 pigeon eval（裁决 020）。真正的 Agent 与验证脚本由假的进程执行器扮演，
/// 只验证跑题器自己的职责：每题一个干净的工作目录、起 Agent 的参数、判分来自验证脚本、工具审计、续跑。
/// </summary>
public class EvalCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-eval-" + Guid.NewGuid().ToString("N"));
    private string Tasks => Path.Combine(_root, "tasks");
    private string Out => Path.Combine(_root, "out");
    private string McpConfig => Path.Combine(_root, "mcp.json");

    public EvalCommandTests()
    {
        Directory.CreateDirectory(Tasks);
        File.WriteAllText(McpConfig, """{"mcpServers":{"patchparity":{"command":"D:\\pp\\bin\\mcp\\patchparity-mcp.exe","args":[]}}}""");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void AddTask(string id, int maxTurns = 80, long wallClockMs = 3_600_000)
    {
        string dir = Path.Combine(Tasks, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.md"), $"# {id} 的题面\n");
        File.WriteAllText(Path.Combine(dir, "verify.mjs"), "// 由假执行器扮演\n");
        File.WriteAllText(Path.Combine(dir, "task.json"), $$$"""
            {"version":1,"id":"{{{id}}}","instructions":"task.md",
             "budget":{"maxTurns":{{{maxTurns}}},"wallClockMs":{{{wallClockMs}}}},
             "verifier":{"command":["node","{TASK_DIR}/verify.mjs"],"timeoutMs":120000}}
            """);
    }

    private string[] Args(params string[] extra) => new[]
    {
        "eval", "--tasks", Tasks, "--out", Out, "--mcp-config", McpConfig,
        "--pigeon", @"D:\pp\pigeon", "--stream-fn", @"D:\pp\pigeon\local\deepseek-stream-fn.mjs",
    }.Concat(extra).ToArray();

    private static bool IsAgent(ProcessSpec spec) => spec.Arguments.Contains("run");

    private static string AgentJson(string session = "sess_1", string status = "completed") =>
        $$"""{"sessionId":"{{session}}","status":"{{status}}","failure":null,"turns":21,"toolCalls":20,""" +
        "\"usage\":{\"totalTokens\":120000,\"cost\":{\"total\":0.0017}}}";

    private static string JudgeJson(string verdict) => $$"""{"case":"x","verdict":"{{verdict}}","matches":true,"observable":true,"scenario":"ground=1"}""";

    /// <summary>Agent 照常收工；验证脚本按给定退出码判分。</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Func<ProcessSpec, ProcessOutcome> _behave;
        public List<ProcessSpec> Specs { get; } = new();

        public FakeRunner(Func<ProcessSpec, ProcessOutcome> behave) => _behave = behave;

        public ProcessOutcome Run(ProcessSpec spec)
        {
            Specs.Add(spec);
            return _behave(spec);
        }
    }

    private static FakeRunner Passing() => new(spec => IsAgent(spec)
        ? new ProcessOutcome(0, false, "日志一行\n" + AgentJson() + "\n")
        : new ProcessOutcome(0, false, "  run/patched patched-1  成功\n判定: 等价\n" + JudgeJson("equivalent") + "\n"));

    private (int Code, string Log) Eval(IProcessRunner runner, params string[] extra)
    {
        var log = new StringWriter();
        int code = EvalCommand.Run(Args(extra), runner, log);
        return (code, log.ToString());
    }

    private JsonObject Result(string id) => JsonNode.Parse(File.ReadAllText(Path.Combine(Out, id, "result.json")))!.AsObject();

    // ── 工作目录与起 Agent ─────────────────────────────────

    [Fact]
    public void 每道题一个工作目录并写好MCP配置与风险档()
    {
        AddTask("safe-a");
        AddTask("safe-b");

        Eval(Passing());

        foreach (string id in new[] { "safe-a", "safe-b" })
        {
            string ws = Path.Combine(Out, id);
            Assert.Equal(File.ReadAllText(McpConfig), File.ReadAllText(Path.Combine(ws, ".mcp.json")));
            JsonNode tiers = JsonNode.Parse(File.ReadAllText(Path.Combine(ws, ".pigeon", "settings.json")))!["mcp"]!["servers"]!["patchparity"]!;
            Assert.Equal("write", (string?)tiers["defaultTier"]);
            Assert.Equal("read", (string?)tiers["tools"]!["list_scenarios"]!["tier"]);
            Assert.Equal("write", (string?)tiers["tools"]!["compare_runs"]!["tier"]);
        }
    }

    [Fact]
    public void 题面经标准输入交给Agent且关掉会话检索()
    {
        AddTask("search-a", maxTurns: 200, wallClockMs: 5_400_000);
        var runner = Passing();

        Eval(runner);

        ProcessSpec agent = runner.Specs.Single(IsAgent);
        string ws = Path.Combine(Out, "search-a");
        Assert.Equal("# search-a 的题面\n", agent.Stdin);
        Assert.Equal(ws, agent.WorkingDirectory);
        List<string> a = agent.Arguments.ToList();
        Assert.Equal(ws, a[a.IndexOf("--root") + 1]);
        Assert.Equal("200", a[a.IndexOf("--max-turns") + 1]);
        Assert.Equal("5400000", a[a.IndexOf("--wall-clock") + 1]);
        // 跨题检索会话在 S6 实测中出现过（裁决 019）：每题自带治理根之外，再关掉检索本身
        Assert.Contains("--no-session-search", a);
        Assert.Contains("--trust-config", a);
        Assert.Contains("--json", a);
        Assert.Contains("--yolo", a);
    }

    [Fact]
    public void 给了推理档位才转给Agent()
    {
        // 5e70ca2 不给档位时对 deepseek-v4-flash 取 high，S5、S6 是在关闭时跑的（云上实测）
        AddTask("safe-a");
        AddTask("safe-b");
        var withThinking = Passing();
        var withoutThinking = Passing();

        Eval(withThinking, "--thinking", "off", "--only", "safe-a");
        Eval(withoutThinking, "--only", "safe-b");

        List<string> a = withThinking.Specs.Single(IsAgent).Arguments.ToList();
        Assert.Equal("off", a[a.IndexOf("--thinking") + 1]);
        Assert.DoesNotContain("--thinking", withoutThinking.Specs.Single(IsAgent).Arguments);
    }

    // ── 判分 ───────────────────────────────────────────────

    [Fact]
    public void 验证脚本在工作目录里跑并据退出码判分()
    {
        AddTask("danger-a");
        AddTask("safe-a");
        var runner = new FakeRunner(spec =>
        {
            if (IsAgent(spec)) return new ProcessOutcome(0, false, AgentJson());
            bool danger = spec.WorkingDirectory.EndsWith("danger-a", StringComparison.Ordinal);
            return danger ? new ProcessOutcome(1, false, JudgeJson("incomplete")) : new ProcessOutcome(0, false, JudgeJson("equivalent"));
        });

        (int code, _) = Eval(runner);

        Assert.Equal(1, code);
        Assert.Equal("fail", (string?)Result("danger-a")["grade"]);
        Assert.Equal("incomplete", (string?)Result("danger-a")["judge"]!["verdict"]);
        Assert.Equal("pass", (string?)Result("safe-a")["grade"]);
        ProcessSpec verify = runner.Specs.Last(s => !IsAgent(s));
        Assert.Equal(Path.Combine(Out, "safe-a"), verify.WorkingDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(Tasks), "safe-a") + "/verify.mjs", verify.Arguments[0]);
    }

    [Fact]
    public void 全部通过时退出码为0且汇总带上判定与用量()
    {
        AddTask("safe-a");

        (int code, string log) = Eval(Passing());

        Assert.Equal(0, code);
        Assert.Contains("判分通过 1 / 1", log);
        string[] csv = File.ReadAllLines(Path.Combine(Out, "summary.csv"));
        Assert.StartsWith("task,grade,verdict,observable", csv[0].TrimStart('\uFEFF'));
        Assert.StartsWith("safe-a,pass,equivalent,true,,ground=1,completed,21,20,120000,0.0017,", csv[1]);
        Assert.Single(File.ReadAllLines(Path.Combine(Out, "results.jsonl")));
    }

    [Fact]
    public void Agent超时也照跑验证脚本并记下超时()
    {
        AddTask("safe-a");
        var runner = new FakeRunner(spec => IsAgent(spec)
            ? new ProcessOutcome(-1, true, "")
            : new ProcessOutcome(1, false, """{"verdict":"no_ledger","matches":false}"""));

        Eval(runner);

        JsonObject r = Result("safe-a");
        Assert.True((bool)r["agent"]!["timed_out"]!);
        Assert.Equal("killed", (string?)r["agent"]!["status"]);
        Assert.Equal("no_ledger", (string?)r["judge"]!["verdict"]);
        Assert.Equal(2, runner.Specs.Count);
    }

    // ── 工具审计 ───────────────────────────────────────────

    [Fact]
    public void 统计工具调用并单列非patchparity的工具()
    {
        AddTask("search-a");
        var runner = new FakeRunner(spec =>
        {
            if (!IsAgent(spec)) return new ProcessOutcome(0, false, JudgeJson("equivalent"));
            // 扮演 Pigeon 5e70ca2：会话存档在治理根（即工作目录）的 .pigeon/state/sessions/<编码的工作目录>/<时间>_<会话号>.jsonl，
            // 格式取自本机冒烟测试的实际存档
            string sessions = Path.Combine(spec.WorkingDirectory, ".pigeon", "state", "sessions", "--D--pp-e-s7-search-a--");
            Directory.CreateDirectory(sessions);
            File.WriteAllLines(Path.Combine(sessions, "2026-10-12T10-09-04-716Z_sess_9.jsonl"), new[]
            {
                """{"kind":"header","version":4,"id":"sess_9"}""",
                """{"kind":"entry","type":"custom","customType":"pigeon.run-start","data":{"policy":{"allow":["read_file"]}}}""",
                """{"kind":"entry","type":"message","message":{"role":"assistant","content":[{"type":"text","text":"试第一个场景"},""" +
                """{"type":"toolCall","id":"1","name":"mcp__patchparity__run_scenario","arguments":{}},""" +
                """{"type":"toolCall","id":"2","name":"mcp__patchparity__run_scenario","arguments":{}}]}}""",
                // 工具返回另记一条，带着工具名，不算一次调用
                """{"kind":"entry","type":"message","message":{"role":"toolResult","toolCallId":"2","toolName":"mcp__patchparity__run_scenario","content":[{"type":"text","text":"ok"}]}}""",
                // 别的条目里出现 toolCall 字样也不算
                """{"kind":"entry","type":"custom","customType":"note","data":{"content":[{"type":"toolCall","name":"mcp__patchparity__run_scenario"}]}}""",
                """{"kind":"entry","type":"message","message":{"role":"assistant","content":[{"type":"toolCall","id":"3","name":"read_file","arguments":{"path":"x"}}]}}""",
            });
            // 另一个会话的存档不算进来
            File.WriteAllLines(Path.Combine(sessions, "2026-10-12T09-00-00-000Z_sess_other.jsonl"), new[]
            {
                """{"kind":"entry","type":"message","message":{"role":"assistant","content":[{"type":"toolCall","id":"9","name":"grep","arguments":{}}]}}""",
            });
            return new ProcessOutcome(0, false, AgentJson(session: "sess_9"));
        });

        (_, string log) = Eval(runner);

        JsonObject r = Result("search-a");
        Assert.Equal(2, (int)r["tools"]!["mcp__patchparity__run_scenario"]!);
        Assert.Equal(1, (int)r["tools"]!["read_file"]!);
        Assert.Equal(new[] { "read_file" }, r["foreign_tools"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("非 patchparity 的工具 read_file", log);
    }

    // ── 续跑与筛选 ─────────────────────────────────────────

    [Fact]
    public void 已有结果的题跳过而中途退出的残局整个重来()
    {
        AddTask("done-a");
        AddTask("half-b");
        Directory.CreateDirectory(Path.Combine(Out, "done-a"));
        File.WriteAllText(Path.Combine(Out, "done-a", "result.json"),
            """{"task":"done-a","grade":"pass","judge":null,"agent":{"status":"completed"},"foreign_tools":[],"wall_seconds":60}""");
        Directory.CreateDirectory(Path.Combine(Out, "half-b"));
        File.WriteAllText(Path.Combine(Out, "half-b", "stale.txt"), "上次中途退出留下的");
        var runner = Passing();

        (int code, string log) = Eval(runner);

        Assert.Equal(0, code);
        Assert.Contains("done-a：已有结果，跳过", log);
        Assert.DoesNotContain(runner.Specs, s => s.WorkingDirectory.EndsWith("done-a", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(Out, "half-b", "stale.txt")));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(Out, "results.jsonl")).Length);
    }

    [Fact]
    public void 只跑id以给定前缀开头的题()
    {
        AddTask("safe-a");
        AddTask("search-a");
        var runner = Passing();

        Eval(runner, "--only", "search-");

        Assert.True(Directory.Exists(Path.Combine(Out, "search-a")));
        Assert.False(Directory.Exists(Path.Combine(Out, "safe-a")));
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("eval|--tasks")]
    [InlineData("eval|--unknown|x")]
    public void 参数不全即为用法错误(string args)
    {
        Assert.Equal(2, EvalCommand.Run(args.Split('|'), Passing(), TextWriter.Null));
    }

    // ── 真实进程 ───────────────────────────────────────────

    [Fact]
    public void 系统执行器把标准输入送给子进程并收下输出()
    {
        string outFile = Path.Combine(_root, "o.txt");

        // more 按系统代码页输出，中文读回会乱码，这里只用 ASCII；真正的子进程（node）一律 UTF-8
        ProcessOutcome outcome = new SystemProcessRunner().Run(new ProcessSpec(
            "cmd.exe", new[] { "/c", "more" }, _root, "task-line-1\n", TimeSpan.FromSeconds(30), outFile, Path.Combine(_root, "e.txt")));

        Assert.False(outcome.TimedOut);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("task-line-1", outcome.Stdout);
        Assert.Contains("task-line-1", File.ReadAllText(outFile));
    }

    [Fact]
    public void 系统执行器超时即结束子进程()
    {
        var started = DateTime.UtcNow;

        ProcessOutcome outcome = new SystemProcessRunner().Run(new ProcessSpec(
            "cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, _root, null, TimeSpan.FromSeconds(1),
            Path.Combine(_root, "o.txt"), Path.Combine(_root, "e.txt")));

        Assert.True(outcome.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }
}

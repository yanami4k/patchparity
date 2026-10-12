using Xunit;
using System.Text;
using System.Text.Json.Nodes;
using PatchParity.Mcp;
using PatchParity.Pipeline;

namespace PatchParity.Mcp.Tests;

public class DispatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-mcp-" + Guid.NewGuid().ToString("N"));
    private readonly FakePatch _patch = new();
    private readonly FakeUnity _unity;
    private readonly Tools _tools;
    private readonly McpDispatcher _dispatcher;
    private readonly WorkspaceLease _lease;
    private string LedgerPath => Path.Combine(_root, "worktree", CaseLedger.FileName);

    private const string Header = "tick,total,c:Item:count,c:Item:hash";
    private const string BaselineCsv = Header + "\n0,1,1,10\n1,2,1,20\n";
    private const string PatchedCsv = Header + "\n0,1,1,10\n1,9,1,99\n";

    public DispatcherTests()
    {
        Directory.CreateDirectory(_root);
        _unity = new FakeUnity(_root);
        var paths = new ToolPaths { UnityExecutable = "unity", ProjectPath = _root, ArtifactRoot = _root };
        var cache = new BaselineCache(Path.Combine(_root, "cache"));
        _unity.Cache = cache;
        _lease = WorkspaceLease.ForProject(_root);

        // 包的键随源码状态变化：施加改动前后各算一次，得到两个不同的键
        _tools = new Tools(
            paths,
            new UnityInvoker(paths, _unity),
            _patch,
            cache,
            () => new CacheKey
            {
                Value = _patch.Applied ? "patched0000" : "baseline000",
                Composition = new[] { new CacheKeyInput("源码", _patch.Applied ? "p" : "b") },
            },
            _lease,
            new CaseLedger(Path.Combine(_root, "worktree", CaseLedger.FileName)));
        _unity.PatchedKey = "patched0000";
        _dispatcher = new McpDispatcher(_tools);
    }

    public void Dispose()
    {
        // 与会话结束走同一条路径：放开被卡住的任务，等后台任务全部结束并撤销改动，再清理目录
        _unity.Hold?.Set();
        _tools.RevertAll();
        _lease.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private static JsonObject Request(string method, JsonObject? parameters = null, int id = 1)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }
        return request;
    }

    private JsonNode Call(string tool, JsonObject arguments)
    {
        JsonObject? response = _dispatcher.Handle(
            Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }));
        Assert.NotNull(response);
        Assert.Null(response!["error"]);
        return response["result"]!;
    }

    private static bool IsError(JsonNode result) => result["isError"]!.GetValue<bool>();

    /// <summary>启动一个后台任务，再用 wait_job 反复等待直到完成，返回完成时的结果。</summary>
    private JsonNode RunJob(string tool, JsonObject arguments)
    {
        JsonNode started = Call(tool, arguments);
        if (IsError(started))
        {
            return started;
        }
        Assert.Equal("running", Structured(started, "status"));
        string jobId = Structured(started, "job_id");

        for (int i = 0; i < 20; i++)
        {
            JsonNode waited = Call("wait_job", new JsonObject { ["job_id"] = jobId, ["max_wait_seconds"] = 5 });
            if (Structured(waited, "status") != "running")
            {
                return waited;
            }
        }
        throw new TimeoutException("后台任务未在预期内完成: " + jobId);
    }

    private static string Structured(JsonNode result, string key) =>
        result["structuredContent"]![key]!.GetValue<string>();

    // ── 协议层 ─────────────────────────────────────────────

    [Fact]
    public void 初始化返回协议版本与工具能力()
    {
        JsonObject? response = _dispatcher.Handle(Request("initialize"));

        JsonNode result = response!["result"]!;
        Assert.Equal(McpDispatcher.ProtocolVersion, result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public void 通知不应答()
    {
        var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };

        Assert.Null(_dispatcher.Handle(notification));
    }

    [Fact]
    public void 工具清单为七个且标注是否只读()
    {
        JsonObject? response = _dispatcher.Handle(Request("tools/list"));

        var tools = (JsonArray)response!["result"]!["tools"]!;
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        bool ReadOnly(string name) => tools
            .First(t => t!["name"]!.GetValue<string>() == name)!["annotations"]!["readOnlyHint"]!.GetValue<bool>();

        Assert.Equal(
            new HashSet<string>
            {
                "list_scenarios", "build_package", "build_patch", "revert_patch", "run_scenario", "wait_job", "compare_runs",
            },
            names);
        Assert.True(ReadOnly("list_scenarios"));
        Assert.True(ReadOnly("compare_runs"));
        Assert.True(ReadOnly("wait_job"));
        Assert.False(ReadOnly("build_package"));
        Assert.False(ReadOnly("build_patch"));
        Assert.False(ReadOnly("revert_patch"));
        Assert.False(ReadOnly("run_scenario"));
    }

    [Fact]
    public void 不支持的方法返回协议错误()
    {
        JsonObject? response = _dispatcher.Handle(Request("resources/list"));

        Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void 未知工具作为工具失败返回而不是协议错误()
    {
        Assert.True(IsError(Call("no_such_tool", new JsonObject())));
    }

    // ── 按真实顺序串联的完整流程 ───────────────────────────
    // 逐个工具的测试曾全部通过，而工具面在真实顺序下走不通。这条测试按一次验证的实际调用顺序串起来。

    [Fact]
    public void 完整流程按真实顺序走得通并判为等价()
    {
        // 一、母包基线：改动施加前构建
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");

        // 二、补丁：施加改动，只重编热更程序集
        Assert.False(IsError(RunJob("build_patch", new JsonObject
        {
            ["patch_path"] = "cases/x.patch",
            ["patch_name"] = "x",
        })));

        // 三、母包加补丁跑两次（第二次用于确定性自检）
        Assert.False(IsError(RunJob("run_scenario", new JsonObject
        {
            ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "patch-1",
        })));
        Assert.False(IsError(RunJob("run_scenario", new JsonObject
        {
            ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "patch-2",
        })));

        // 四、整包重编：改动仍处于施加状态，再构建一次得到另一个包
        string rebuild = Structured(RunJob("build_package", new JsonObject()), "package");
        Assert.NotEqual(baseline, rebuild);

        Assert.False(IsError(RunJob("run_scenario", new JsonObject
        {
            ["package"] = rebuild, ["run_name"] = "rebuild",
        })));

        // 五、确定性自检与判定
        JsonNode determinism = Call("compare_runs", new JsonObject { ["left"] = "patch-1", ["right"] = "patch-2" });
        JsonNode verdict = Call("compare_runs", new JsonObject
        {
            ["left"] = "rebuild", ["right"] = "patch-1", ["touched_components"] = "Item",
        });

        Assert.Equal("equivalent", Structured(determinism, "verdict"));
        Assert.Equal("equivalent", Structured(verdict, "verdict"));

        // 六、撤销改动
        Assert.False(IsError(Call("revert_patch", new JsonObject { ["patch_path"] = "cases/x.patch" })));
        Assert.False(_patch.Applied);
        Assert.Empty(_tools.AppliedPatches);
    }

    [Fact]
    public void 按真实顺序跑完后从台账还原出的判定为等价()
    {
        // MCP 一路的判定不采信 Agent，只看服务端记下的事实（裁决 009）
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");
        RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });
        RunJob("run_scenario", new JsonObject { ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "patch-1" });
        RunJob("run_scenario", new JsonObject { ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "patch-2" });
        string rebuild = Structured(RunJob("build_package", new JsonObject()), "package");
        RunJob("run_scenario", new JsonObject { ["package"] = rebuild, ["run_name"] = "rebuild" });
        Call("revert_patch", new JsonObject { ["patch_path"] = "cases/x.patch" });

        IReadOnlyList<LedgerEntry> entries = CaseLedger.Load(LedgerPath);
        Assert.Equal(
            new[] { "baseline_build", "patch_compile", "run/patched", "run/patched", "rebuild_build", "run/rebuild" },
            entries.Select(e => e.RunKind is null ? e.Step : $"{e.Step}/{e.RunKind}"));

        (CaseEvidence evidence, string? inconsistency) = CaseLedger.ToEvidence(entries, new[] { "Item" });
        Assert.Null(inconsistency);
        Assert.Equal(Verdict.Equivalent, CaseVerdict.Decide(evidence).Verdict);
    }

    [Fact]
    public void 改动未施加时构建的包记为母包基线施加后记为整包重编()
    {
        RunJob("build_package", new JsonObject());
        RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });
        RunJob("build_package", new JsonObject());
        Call("revert_patch", new JsonObject { ["patch_path"] = "cases/x.patch" });
        // 撤销之后再构建，又回到母包基线：命中缓存
        RunJob("build_package", new JsonObject());

        IReadOnlyList<LedgerEntry> entries = CaseLedger.Load(LedgerPath);
        Assert.Equal(new[] { "baseline_build", "patch_compile", "rebuild_build", "baseline_build" }, entries.Select(e => e.Step));
        Assert.True(entries[3].CacheHit);
    }

    [Fact]
    public void 整包构建失败也记账()
    {
        _unity.BuildFails = true;

        RunJob("build_package", new JsonObject());

        LedgerEntry build = Assert.Single(CaseLedger.Load(LedgerPath));
        Assert.Equal(LedgerSteps.BaselineBuild, build.Step);
        Assert.False(build.Ok);
    }

    [Fact]
    public void 失败的步骤也记账()
    {
        _unity.CompileFails = true;

        RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });

        LedgerEntry compile = Assert.Single(CaseLedger.Load(LedgerPath));
        Assert.False(compile.Ok);
        Assert.Equal("Build", compile.FailureStage);
    }

    [Fact]
    public void 补丁编译不写入任何缓存中的包()
    {
        // 缓存里的包内容必须与其键一致；补丁若写进母包基线，缓存条目就被污染了
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");
        RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });

        string cacheRoot = Path.Combine(_root, "cache");
        Assert.NotEmpty(_unity.CompileTargets);
        Assert.All(_unity.CompileTargets, target =>
            Assert.False(Path.GetFullPath(target).StartsWith(Path.GetFullPath(cacheRoot), StringComparison.OrdinalIgnoreCase)));
        Assert.False(Directory.Exists(Path.Combine(_root, "cache", baseline, "HotUpdateDlls")));
    }

    [Fact]
    public void 带补丁运行时从补丁目录加载热更程序集()
    {
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");
        RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });
        RunJob("run_scenario", new JsonObject { ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "r" });

        string dlls = _unity.RunDllDirs.Last();
        Assert.Contains(Path.Combine("patches", "x"), dlls);
    }

    [Fact]
    public void 会话结束时兜底撤销未撤销的改动()
    {
        RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });
        Assert.True(_patch.Applied);

        IReadOnlyList<string> failures = _tools.RevertAll();

        Assert.Empty(failures);
        Assert.False(_patch.Applied);
        Assert.Empty(_tools.AppliedPatches);
    }

    [Fact]
    public void 补丁编译失败时改动仍记为已施加以便撤销()
    {
        _unity.CompileFails = true;

        JsonNode result = RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });

        Assert.True(IsError(result));
        Assert.Contains("cases/x.patch", _tools.AppliedPatches);
    }

    // ── 后台任务 ───────────────────────────────────────────
    // MCP 客户端普遍对单次请求设 60 秒上限，整包构建要十余分钟，启动 Unity 的工具必须立即返回

    [Fact]
    public void 启动构建立即返回任务号而不等构建完成()
    {
        _unity.Hold = new ManualResetEventSlim(false);

        JsonNode started = Call("build_package", new JsonObject());

        Assert.False(IsError(started));
        Assert.Equal("running", Structured(started, "status"));
        Assert.StartsWith("job-", Structured(started, "job_id"));
        _unity.Hold.Set();
    }

    [Fact]
    public void 任务未完成时等待返回running()
    {
        _unity.Hold = new ManualResetEventSlim(false);
        string jobId = Structured(Call("build_package", new JsonObject()), "job_id");

        JsonNode waited = Call("wait_job", new JsonObject { ["job_id"] = jobId, ["max_wait_seconds"] = 1 });

        Assert.False(IsError(waited));
        Assert.Equal("running", Structured(waited, "status"));
        _unity.Hold.Set();
    }

    [Fact]
    public void 任务完成后等待交回完整结果()
    {
        JsonNode done = RunJob("build_package", new JsonObject());

        Assert.Equal("succeeded", Structured(done, "status"));
        Assert.False(string.IsNullOrEmpty(Structured(done, "package")));
        Assert.NotNull(done["structuredContent"]!["evidence"]);
    }

    [Fact]
    public void 同一时刻只允许一个会改动工程的任务()
    {
        // 同一个 Unity 工程目录同时只能开一个 Unity；撞上即拒绝，不排队
        _unity.Hold = new ManualResetEventSlim(false);
        Call("build_package", new JsonObject());

        JsonNode second = Call("build_patch", new JsonObject { ["patch_path"] = "p", ["patch_name"] = "x" });

        Assert.True(IsError(second));
        Assert.Contains("wait_job", second["content"]![0]!["text"]!.GetValue<string>());
        _unity.Hold.Set();
    }

    [Fact]
    public void 任务运行中拒绝撤销改动()
    {
        _unity.Hold = new ManualResetEventSlim(false);
        Call("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });

        JsonNode revert = Call("revert_patch", new JsonObject { ["patch_path"] = "cases/x.patch" });

        Assert.True(IsError(revert));
        _unity.Hold.Set();
    }

    [Fact]
    public void 未知任务号被拒绝()
    {
        Assert.True(IsError(Call("wait_job", new JsonObject { ["job_id"] = "job-999-none" })));
    }

    [Fact]
    public void 后台异常作为工具失败交回()
    {
        _unity.ThrowOnLaunch = true;

        JsonNode done = RunJob("build_package", new JsonObject());

        Assert.Equal("failed", Structured(done, "status"));
        Assert.True(IsError(done));
    }

    // ── 会话之间的隔离 ─────────────────────────────────────
    // 源码状态跨多次调用保持，另一个会话插进来会把别人的改动当成基线或把两份改动叠在一起，且不报错

    [Theory]
    [InlineData("build_package")]
    [InlineData("build_patch")]
    [InlineData("revert_patch")]
    public void 另一个会话持有工作区时改动工程的工具被拒绝(string tool)
    {
        using WorkspaceLease other = WorkspaceLease.ForProject(_root);
        Assert.True(other.TryAcquire(out _));

        JsonNode result = Call(tool, new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });

        Assert.True(IsError(result));
        Assert.Contains("另一个会话占用", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(_patch.Applied);
        Assert.Empty(_unity.CompileTargets);
    }

    [Fact]
    public void 另一个会话持有工作区时只读工具照常可用()
    {
        using WorkspaceLease other = WorkspaceLease.ForProject(_root);
        Assert.True(other.TryAcquire(out _));
        WriteRun("a", BaselineCsv);
        WriteRun("b", BaselineCsv);

        Assert.False(IsError(Call("list_scenarios", new JsonObject())));
        Assert.False(IsError(Call("compare_runs", new JsonObject { ["left"] = "a", ["right"] = "b" })));
    }

    [Fact]
    public void 本会话取得工作区后另一个会话拿不到()
    {
        RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });
        Call("revert_patch", new JsonObject { ["patch_path"] = "cases/x.patch" });

        // 撤销之后仍持有：租约以会话为单位，持有到会话结束
        using WorkspaceLease other = WorkspaceLease.ForProject(_root);
        Assert.False(other.TryAcquire(out _));
    }

    // ── 开工前恢复 ─────────────────────────────────────────
    // 上一个持有者未必走完了收尾：Pigeon 关闭服务端时只等 2 秒就强杀

    [Fact]
    public void 拿到工作区后先恢复再动手且只恢复一次()
    {
        RunJob("build_package", new JsonObject());
        RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });

        Assert.Equal(1, _patch.Restores);
    }

    [Fact]
    public void 工作区有无法恢复的残留时拒绝开工且不施加改动()
    {
        _patch.RestoreProblem = "玩法源码有来源不明的改动";

        JsonNode result = Call("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });

        Assert.True(IsError(result));
        Assert.Contains("来源不明", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(_patch.Applied);
        Assert.Empty(_unity.CompileTargets);
    }

    [Fact]
    public void 恢复失败后下次调用重试()
    {
        _patch.RestoreProblem = "残留";
        Assert.True(IsError(Call("build_package", new JsonObject())));

        _patch.RestoreProblem = null;

        Assert.False(IsError(RunJob("build_package", new JsonObject())));
        Assert.Equal(2, _patch.Restores);
    }

    [Fact]
    public void 只读工具不触发恢复()
    {
        Call("list_scenarios", new JsonObject());
        WriteRun("a", BaselineCsv);
        WriteRun("b", BaselineCsv);
        Call("compare_runs", new JsonObject { ["left"] = "a", ["right"] = "b" });

        Assert.Equal(0, _patch.Restores);
    }

    [Fact]
    public void 单次等待的上限给客户端的60秒请求超时留足余量()
    {
        // Pigeon 调用 MCP 工具落在 SDK 缺省的 60 秒；构建时主机满载，留 15 秒
        Assert.True(Tools.MaxWaitSeconds <= 45);
    }

    // ── 自定义场景与场景搜索（裁决 019）────────────────────

    [Fact]
    public void 自定义场景经规范化后传给验证包并记进台账()
    {
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");

        JsonNode run = RunJob("run_scenario", new JsonObject
        {
            ["package"] = baseline, ["run_name"] = "b", ["options"] = "chase=1;ground=1;moveSpeed=5",
        });

        Assert.False(IsError(run));
        string canonical = "seed=42;ticks=600;players=2;bots=0;maxEntities=512;deltaTimeMs=25;ground=1;moveSpeed=5;chase=1";
        Assert.Equal(canonical, _unity.RunScenarios.Last());
        Assert.Equal(canonical, CaseLedger.Load(LedgerPath).Last().Scenario);
    }

    [Fact]
    public void 预置场景与参数相同的自定义场景在台账里是同一场景()
    {
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");
        RunJob("run_scenario", new JsonObject { ["package"] = baseline, ["run_name"] = "a", ["scenario"] = "seed-7" });
        RunJob("run_scenario", new JsonObject { ["package"] = baseline, ["run_name"] = "b", ["options"] = "seed=7" });

        IReadOnlyList<LedgerEntry> entries = CaseLedger.Load(LedgerPath);
        Assert.Equal(entries[^2].Scenario, entries[^1].Scenario);
    }

    [Theory]
    [InlineData("speed=5")]
    [InlineData("moveSpeed=99")]
    [InlineData("classes=0")]
    public void 不合法的自定义场景在启动前被拒绝(string options)
    {
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");

        JsonNode run = Call("run_scenario", new JsonObject { ["package"] = baseline, ["run_name"] = "x", ["options"] = options });

        Assert.True(IsError(run));
        Assert.Contains("自定义场景不合法", run["content"]![0]!["text"]!.GetValue<string>());
        Assert.Empty(_unity.RunScenarios);
    }

    [Fact]
    public void 预置场景与自定义场景不能同时给()
    {
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");

        Assert.True(IsError(Call("run_scenario", new JsonObject
        {
            ["package"] = baseline, ["run_name"] = "x", ["scenario"] = "default", ["options"] = "ground=1",
        })));
    }

    [Fact]
    public void 列场景时给出自定义参数的说明()
    {
        JsonNode result = Call("list_scenarios", new JsonObject());

        JsonObject parameters = result["structuredContent"]!["custom_parameters"]!.AsObject();
        Assert.True(parameters.ContainsKey("ground"));
        Assert.True(parameters.ContainsKey("chase"));
    }

    [Fact]
    public void 参数说明里的示例不替Agent解题()
    {
        // S6 首轮：示例恰好是一道题的探测场景，Agent 第一步读了说明就照抄，场景搜索没被真正考到（裁决 019）
        string text = Call("list_scenarios", new JsonObject())["content"]![0]!["text"]!.GetValue<string>();

        foreach (string giveaway in new[] { "ground=1", "moveSpeed=", "chase=1", "attack=", "jump=", "spawn=" })
        {
            Assert.DoesNotContain(giveaway, text);
        }
    }

    [Fact]
    public void 场景搜索后在选定场景下验证且台账能证明改动可观测()
    {
        // 先在缺省场景下试母包与整包重编，再换一个场景，最后在该场景下跑补丁：judge 应以最后的场景判定
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");
        RunJob("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });
        string rebuild = Structured(RunJob("build_package", new JsonObject()), "package");
        const string chosen = "ground=1;moveSpeed=5";
        RunJob("run_scenario", new JsonObject { ["package"] = baseline, ["run_name"] = "b-default" });
        RunJob("run_scenario", new JsonObject { ["package"] = rebuild, ["run_name"] = "r-default" });
        RunJob("run_scenario", new JsonObject { ["package"] = baseline, ["run_name"] = "b-ground", ["options"] = chosen });
        RunJob("run_scenario", new JsonObject { ["package"] = rebuild, ["run_name"] = "r-ground", ["options"] = chosen });
        RunJob("run_scenario", new JsonObject { ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "p1", ["options"] = chosen });
        RunJob("run_scenario", new JsonObject { ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "p2", ["options"] = chosen });
        Call("revert_patch", new JsonObject { ["patch_path"] = "cases/x.patch" });

        IReadOnlyList<LedgerEntry> entries = CaseLedger.Load(LedgerPath);
        (CaseEvidence evidence, string? inconsistency) = CaseLedger.ToEvidence(entries, new[] { "Item" });
        CaseDecision decision = CaseVerdict.Decide(evidence, requireObservable: true);

        Assert.Null(inconsistency);
        Assert.Contains("ground=1", CaseLedger.ChosenScenario(entries));
        Assert.Equal(Verdict.Equivalent, decision.Verdict);
        Assert.True(decision.Observable);
    }

    // ── 入参校验 ───────────────────────────────────────────

    [Fact]
    public void 在未构建的包上运行被拒绝()
    {
        JsonNode result = RunJob("run_scenario", new JsonObject { ["package"] = "nosuchpackage", ["run_name"] = "r" });

        Assert.True(IsError(result));
        Assert.Contains("build_package", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void 引用不存在的补丁被拒绝()
    {
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");

        JsonNode result = RunJob("run_scenario", new JsonObject
        {
            ["package"] = baseline, ["patch_name"] = "missing", ["run_name"] = "r",
        });

        Assert.True(IsError(result));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void 能跳出产物目录的名字被拒绝(string name)
    {
        Assert.True(IsError(RunJob("build_patch", new JsonObject { ["patch_path"] = "p", ["patch_name"] = name })));
        Assert.True(IsError(Call("compare_runs", new JsonObject { ["left"] = name, ["right"] = "b" })));
    }

    [Fact]
    public void 能跳出缓存目录的包标识被拒绝()
    {
        Assert.True(IsError(RunJob("run_scenario", new JsonObject { ["package"] = "..", ["run_name"] = "r" })));
    }

    [Fact]
    public void 未知场景名被拒绝()
    {
        string baseline = Structured(RunJob("build_package", new JsonObject()), "package");

        Assert.True(IsError(RunJob("run_scenario", new JsonObject
        {
            ["package"] = baseline, ["run_name"] = "r", ["scenario"] = "不存在的场景",
        })));
    }

    // ── 比对 ───────────────────────────────────────────────

    [Fact]
    public void 比对两次运行给出分歧位置()
    {
        WriteRun("left", BaselineCsv);
        WriteRun("right", PatchedCsv);

        JsonNode result = Call("compare_runs", new JsonObject { ["left"] = "left", ["right"] = "right" });

        Assert.Equal("divergent", Structured(result, "verdict"));
        Assert.Equal(1, result["structuredContent"]!["first_divergent_tick"]!.GetValue<int>());
    }

    [Fact]
    public void 覆盖不过时比对判为作废而不是一致()
    {
        WriteRun("a", Header + "\n0,1,0,0\n");
        WriteRun("b", Header + "\n0,1,0,0\n");

        JsonNode result = Call("compare_runs", new JsonObject { ["left"] = "a", ["right"] = "b" });

        Assert.Equal("void", Structured(result, "verdict"));
    }

    [Fact]
    public void 判为不等价不算工具出错()
    {
        // 场景搜索时母包与整包重编不同正是要找的结果；记成出错会误导 Agent，也让 eval 的出错统计失真（S6 实测）
        WriteRun("left", BaselineCsv);
        WriteRun("right", PatchedCsv);

        JsonNode result = Call("compare_runs", new JsonObject { ["left"] = "left", ["right"] = "right" });

        Assert.Equal("divergent", Structured(result, "verdict"));
        Assert.False(IsError(result));
    }

    [Fact]
    public void 改动触及的组件名不认识时拒绝并列出可用的组件()
    {
        // S6 实测：Agent 填了系统名，覆盖自检静默不过、连得三次作废
        WriteRun("a", BaselineCsv);
        WriteRun("b", BaselineCsv);

        JsonNode result = Call("compare_runs", new JsonObject
        {
            ["left"] = "a", ["right"] = "b", ["touched_components"] = "PlatformerCommandSystem",
        });

        Assert.True(IsError(result));
        string text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("PlatformerCommandSystem", text);
        Assert.Contains("可用的组件：Item", text);
    }

    [Fact]
    public void 覆盖自检不过时说明原因()
    {
        // 组件认识，但整个运行里一个实例都没有：照常比对，判为作废并说明是哪个组件没出现
        const string header = "tick,total,c:Item:count,c:Item:hash,c:Shield:count,c:Shield:hash";
        WriteRun("a", header + "\n0,1,1,10,0,0\n");
        WriteRun("b", header + "\n0,1,1,10,0,0\n");

        JsonNode result = Call("compare_runs", new JsonObject
        {
            ["left"] = "a", ["right"] = "b", ["touched_components"] = "Item, Shield",
        });

        Assert.False(IsError(result));
        Assert.Equal("void", Structured(result, "verdict"));
        Assert.Contains("从未出现: Shield", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void 比对结果放进evidence键()
    {
        WriteRun("a", BaselineCsv);
        WriteRun("b", BaselineCsv);

        JsonNode result = Call("compare_runs", new JsonObject { ["left"] = "a", ["right"] = "b" });

        string evidence = Structured(result, "evidence");
        Assert.Contains("判定", evidence);
        Assert.Contains("覆盖自检", evidence);
    }

    [Fact]
    public void 列场景返回预置场景()
    {
        JsonNode result = Call("list_scenarios", new JsonObject());

        Assert.Equal(Tools.Scenarios.Count, ((JsonArray)result["structuredContent"]!["scenarios"]!).Count);
    }

    [Fact]
    public void 超长证据被截断且标注()
    {
        string truncated = ToolResult.Truncate(new string('甲', 20000));

        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= ToolResult.EvidenceLimitBytes);
        Assert.EndsWith("已截断", truncated);
    }

    [Fact]
    public void 未超限的证据原样保留()
    {
        Assert.Equal("判定 equivalent", ToolResult.Truncate("判定 equivalent"));
    }

    private void WriteRun(string name, string csv)
    {
        string dir = Path.Combine(_root, "runs", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hashes.csv"), csv);
    }

    private sealed class FakePatch : IPatchSource
    {
        public bool Applied { get; private set; }
        public int Restores { get; private set; }

        /// <summary>设置后，开工前的恢复报告这个问题，即工作区有无法恢复的残留。</summary>
        public string? RestoreProblem { get; set; }

        public void Apply(ChangeCase change) => Applied = true;
        public void Revert(ChangeCase change) => Applied = false;

        public string? RestoreWorkspace()
        {
            Restores++;
            return RestoreProblem;
        }
    }

    /// <summary>
    /// 模拟 Unity：构建写出构建报告，编译写出编译报告与热更程序集目录，运行按加载的是不是补丁决定行为。
    /// 补丁与整包重编的行为相同，母包自身的行为不同——对应一个真正改变了行为的安全改动。
    /// </summary>
    private sealed class FakeUnity : IProcessLauncher
    {
        private readonly string _root;
        public FakeUnity(string root) => _root = root;

        public BaselineCache? Cache { get; set; }
        public string PatchedKey { get; set; } = "";
        public bool CompileFails { get; set; }
        public bool BuildFails { get; set; }

        /// <summary>设置后，等待产物时先卡在这里，用于检验"立即返回"与互斥。</summary>
        public ManualResetEventSlim? Hold { get; set; }

        public bool ThrowOnLaunch { get; set; }
        public List<string> CompileTargets { get; } = new();
        public List<string> RunDllDirs { get; } = new();
        public List<string> RunScenarios { get; } = new();

        public void LaunchDetached(string executable, IReadOnlyList<string> arguments, string? outputLog = null)
        {
            if (ThrowOnLaunch)
            {
                throw new InvalidOperationException("模拟的启动失败");
            }
            if (executable.EndsWith("Verify.exe", StringComparison.Ordinal))
            {
                string dlls = ValueOf(arguments, "-patchparityDlls")!;
                RunDllDirs.Add(dlls);
                RunScenarios.Add(ValueOf(arguments, "-patchparityScenario")!);
                bool patched = dlls.Contains(Path.Combine("patches", ""), StringComparison.Ordinal) ||
                               executable.Contains(PatchedKey, StringComparison.Ordinal);

                string result = ValueOf(arguments, "-patchparityResult")!;
                string csv = ValueOf(arguments, "-patchparityCsv")!;
                Directory.CreateDirectory(Path.GetDirectoryName(result)!);
                File.WriteAllText(csv, patched ? PatchedCsv : BaselineCsv);
                File.WriteAllText(result, "OK ticks=2 lastLiveComponents=1\n");
                return;
            }

            string method = ValueOf(arguments, "-executeMethod") ?? "";
            if (method.Contains("BuildVerifyPlayer", StringComparison.Ordinal))
            {
                string output = ValueOf(arguments, "-patchparityOutput")!;
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "verify-build.txt"), BuildFails ? "result=Failed\n" : "result=Succeeded\n");
            }
            else if (method.Contains("CompileHotUpdate", StringComparison.Ordinal))
            {
                string target = ValueOf(arguments, "-patchparityTarget")!;
                CompileTargets.Add(target);
                string report = ValueOf(arguments, "-patchparityOutput")!;
                Directory.CreateDirectory(report);
                if (CompileFails)
                {
                    File.WriteAllText(Path.Combine(report, "patch-compile.txt"), "ERROR 编译失败\n");
                    return;
                }
                Directory.CreateDirectory(Path.Combine(target, "HotUpdateDlls"));
                File.WriteAllText(Path.Combine(report, "patch-compile.txt"), "compileSeconds=1.0\n");
            }
        }

        public bool WaitForArtifact(string artifactPath, TimeSpan timeout)
        {
            Hold?.Wait(TimeSpan.FromSeconds(15));
            return File.Exists(artifactPath);
        }

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

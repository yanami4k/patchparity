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
            });
        _unity.PatchedKey = "patched0000";
        _dispatcher = new McpDispatcher(_tools);
    }

    public void Dispose()
    {
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
    public void 工具清单为六个且标注是否只读()
    {
        JsonObject? response = _dispatcher.Handle(Request("tools/list"));

        var tools = (JsonArray)response!["result"]!["tools"]!;
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        bool ReadOnly(string name) => tools
            .First(t => t!["name"]!.GetValue<string>() == name)!["annotations"]!["readOnlyHint"]!.GetValue<bool>();

        Assert.Equal(
            new HashSet<string> { "list_scenarios", "build_package", "build_patch", "revert_patch", "run_scenario", "compare_runs" },
            names);
        Assert.True(ReadOnly("list_scenarios"));
        Assert.True(ReadOnly("compare_runs"));
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
        string baseline = Structured(Call("build_package", new JsonObject()), "package");

        // 二、补丁：施加改动，只重编热更程序集
        Assert.False(IsError(Call("build_patch", new JsonObject
        {
            ["patch_path"] = "cases/x.patch",
            ["patch_name"] = "x",
        })));

        // 三、母包加补丁跑两次（第二次用于确定性自检）
        Assert.False(IsError(Call("run_scenario", new JsonObject
        {
            ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "patch-1",
        })));
        Assert.False(IsError(Call("run_scenario", new JsonObject
        {
            ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "patch-2",
        })));

        // 四、整包重编：改动仍处于施加状态，再构建一次得到另一个包
        string rebuild = Structured(Call("build_package", new JsonObject()), "package");
        Assert.NotEqual(baseline, rebuild);

        Assert.False(IsError(Call("run_scenario", new JsonObject
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
    public void 补丁编译不写入任何缓存中的包()
    {
        // 缓存里的包内容必须与其键一致；补丁若写进母包基线，缓存条目就被污染了
        string baseline = Structured(Call("build_package", new JsonObject()), "package");
        Call("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });

        string cacheRoot = Path.Combine(_root, "cache");
        Assert.NotEmpty(_unity.CompileTargets);
        Assert.All(_unity.CompileTargets, target =>
            Assert.False(Path.GetFullPath(target).StartsWith(Path.GetFullPath(cacheRoot), StringComparison.OrdinalIgnoreCase)));
        Assert.False(Directory.Exists(Path.Combine(_root, "cache", baseline, "HotUpdateDlls")));
    }

    [Fact]
    public void 带补丁运行时从补丁目录加载热更程序集()
    {
        string baseline = Structured(Call("build_package", new JsonObject()), "package");
        Call("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });
        Call("run_scenario", new JsonObject { ["package"] = baseline, ["patch_name"] = "x", ["run_name"] = "r" });

        string dlls = _unity.RunDllDirs.Last();
        Assert.Contains(Path.Combine("patches", "x"), dlls);
    }

    [Fact]
    public void 会话结束时兜底撤销未撤销的改动()
    {
        Call("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });
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

        JsonNode result = Call("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch", ["patch_name"] = "x" });

        Assert.True(IsError(result));
        Assert.Contains("cases/x.patch", _tools.AppliedPatches);
    }

    // ── 入参校验 ───────────────────────────────────────────

    [Fact]
    public void 在未构建的包上运行被拒绝()
    {
        JsonNode result = Call("run_scenario", new JsonObject { ["package"] = "nosuchpackage", ["run_name"] = "r" });

        Assert.True(IsError(result));
        Assert.Contains("build_package", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void 引用不存在的补丁被拒绝()
    {
        string baseline = Structured(Call("build_package", new JsonObject()), "package");

        JsonNode result = Call("run_scenario", new JsonObject
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
        Assert.True(IsError(Call("build_patch", new JsonObject { ["patch_path"] = "p", ["patch_name"] = name })));
        Assert.True(IsError(Call("compare_runs", new JsonObject { ["left"] = name, ["right"] = "b" })));
    }

    [Fact]
    public void 能跳出缓存目录的包标识被拒绝()
    {
        Assert.True(IsError(Call("run_scenario", new JsonObject { ["package"] = "..", ["run_name"] = "r" })));
    }

    [Fact]
    public void 未知场景名被拒绝()
    {
        string baseline = Structured(Call("build_package", new JsonObject()), "package");

        Assert.True(IsError(Call("run_scenario", new JsonObject
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
        public void Apply(ChangeCase change) => Applied = true;
        public void Revert(ChangeCase change) => Applied = false;
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
        public List<string> CompileTargets { get; } = new();
        public List<string> RunDllDirs { get; } = new();

        public void LaunchDetached(string executable, IReadOnlyList<string> arguments)
        {
            if (executable.EndsWith("Verify.exe", StringComparison.Ordinal))
            {
                string dlls = ValueOf(arguments, "-patchparityDlls")!;
                RunDllDirs.Add(dlls);
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
                File.WriteAllText(Path.Combine(output, "verify-build.txt"), "result=Succeeded\n");
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

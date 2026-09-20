using Xunit;
using System.Text;
using System.Text.Json.Nodes;
using PatchParity.Mcp;
using PatchParity.Pipeline;

namespace PatchParity.Mcp.Tests;

public class DispatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-mcp-" + Guid.NewGuid().ToString("N"));

    public DispatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private McpDispatcher Dispatcher()
    {
        var paths = new ToolPaths { UnityExecutable = "unity", ProjectPath = _root, ArtifactRoot = _root };
        var tools = new Tools(
            paths,
            new UnityInvoker(paths, new NeverLaunches()),
            new NoPatch(),
            new BaselineCache(Path.Combine(_root, "cache")),
            () => new CacheKey { Value = "key", Composition = new[] { new CacheKeyInput("源码", "aaa") } });
        return new McpDispatcher(tools);
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

    private static JsonObject Call(string tool, JsonObject arguments) =>
        Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });

    [Fact]
    public void 初始化返回协议版本与工具能力()
    {
        JsonObject? response = Dispatcher().Handle(Request("initialize"));

        Assert.NotNull(response);
        JsonNode result = response!["result"]!;
        Assert.Equal(McpDispatcher.ProtocolVersion, result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public void 通知不应答()
    {
        // 无 id 即通知，按协议不得应答
        var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };

        Assert.Null(Dispatcher().Handle(notification));
    }

    [Fact]
    public void 工具清单含五个工具且标注是否只读()
    {
        JsonObject? response = Dispatcher().Handle(Request("tools/list"));

        var tools = (JsonArray)response!["result"]!["tools"]!;
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();

        Assert.Equal(5, tools.Count);
        Assert.Contains("list_scenarios", names);
        Assert.Contains("build_baseline", names);
        Assert.Contains("build_patch", names);
        Assert.Contains("run_scenario", names);
        Assert.Contains("compare_runs", names);
        Assert.All(tools, t => Assert.NotNull(t!["annotations"]!["readOnlyHint"]));
    }

    [Fact]
    public void 只读工具被正确标注()
    {
        JsonObject? response = Dispatcher().Handle(Request("tools/list"));
        var tools = (JsonArray)response!["result"]!["tools"]!;

        bool ReadOnly(string name) => tools
            .First(t => t!["name"]!.GetValue<string>() == name)!["annotations"]!["readOnlyHint"]!.GetValue<bool>();

        Assert.True(ReadOnly("list_scenarios"));
        Assert.True(ReadOnly("compare_runs"));
        Assert.False(ReadOnly("build_baseline"));
        Assert.False(ReadOnly("build_patch"));
    }

    [Fact]
    public void 不支持的方法返回协议错误()
    {
        JsonObject? response = Dispatcher().Handle(Request("resources/list"));

        Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void 未知工具作为工具失败返回而不是协议错误()
    {
        // 调用方仍需拿到可读的失败说明，故不上升为协议错误
        JsonObject? response = Dispatcher().Handle(Call("no_such_tool", new JsonObject()));

        Assert.Null(response!["error"]);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void 列场景返回预置场景()
    {
        JsonObject? response = Dispatcher().Handle(Call("list_scenarios", new JsonObject()));

        var scenarios = (JsonArray)response!["result"]!["structuredContent"]!["scenarios"]!;
        Assert.Equal(Tools.Scenarios.Count, scenarios.Count);
    }

    [Fact]
    public void 缺少必填入参时返回工具失败()
    {
        JsonObject? response = Dispatcher().Handle(Call("build_patch", new JsonObject()));

        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void 母包基线未构建时不允许打补丁()
    {
        JsonObject? response = Dispatcher().Handle(
            Call("build_patch", new JsonObject { ["patch_path"] = "cases/x.patch" }));

        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("build_baseline", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void 比对两次运行给出判定与分歧层()
    {
        WriteRun("left", "tick,total,c:Item:count,c:Item:hash\n0,1,1,10\n1,2,1,20\n");
        WriteRun("right", "tick,total,c:Item:count,c:Item:hash\n0,1,1,10\n1,9,1,99\n");

        JsonObject? response = Dispatcher().Handle(Call("compare_runs", new JsonObject
        {
            ["left"] = "left",
            ["right"] = "right",
        }));

        JsonNode structured = response!["result"]!["structuredContent"]!;
        Assert.Equal("divergent", structured["verdict"]!.GetValue<string>());
        Assert.Equal(1, structured["first_divergent_tick"]!.GetValue<int>());
        Assert.Single((JsonArray)structured["divergent_layers"]!);
    }

    [Fact]
    public void 覆盖不过时比对判为作废而不是一致()
    {
        // 两路完全相同但世界是空的：这种一致不构成补丁等价的证据
        WriteRun("a", "tick,total,c:Item:count,c:Item:hash\n0,1,0,0\n");
        WriteRun("b", "tick,total,c:Item:count,c:Item:hash\n0,1,0,0\n");

        JsonObject? response = Dispatcher().Handle(Call("compare_runs", new JsonObject
        {
            ["left"] = "a",
            ["right"] = "b",
        }));

        Assert.Equal("void", response!["result"]!["structuredContent"]!["verdict"]!.GetValue<string>());
    }

    [Fact]
    public void 比对结果放进evidence键()
    {
        WriteRun("a", "tick,total,c:Item:count,c:Item:hash\n0,1,1,10\n");
        WriteRun("b", "tick,total,c:Item:count,c:Item:hash\n0,1,1,10\n");

        JsonObject? response = Dispatcher().Handle(Call("compare_runs", new JsonObject
        {
            ["left"] = "a",
            ["right"] = "b",
        }));

        string evidence = response!["result"]!["structuredContent"]!["evidence"]!.GetValue<string>();
        Assert.Contains("判定", evidence);
        Assert.Contains("覆盖自检", evidence);
    }

    [Fact]
    public void 找不到运行产物时提示先跑场景()
    {
        JsonObject? response = Dispatcher().Handle(Call("compare_runs", new JsonObject
        {
            ["left"] = "missing-a",
            ["right"] = "missing-b",
        }));

        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void 未知场景名被拒绝()
    {
        JsonObject? response = Dispatcher().Handle(Call("run_scenario", new JsonObject
        {
            ["run_name"] = "x",
            ["scenario"] = "不存在的场景",
        }));

        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void 超长证据被截断且标注()
    {
        string huge = new('甲', 20000);

        string truncated = ToolResult.Truncate(huge);

        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= ToolResult.EvidenceLimitBytes);
        Assert.EndsWith("已截断", truncated);
    }

    [Fact]
    public void 未超限的证据原样保留()
    {
        string text = "判定 equivalent";

        Assert.Equal(text, ToolResult.Truncate(text));
    }

    private void WriteRun(string name, string csv)
    {
        string dir = Path.Combine(_root, "run-" + name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hashes.csv"), csv);
    }

    /// <summary>不真正启动进程：协议层测试不应触碰 Unity。</summary>
    private sealed class NeverLaunches : IProcessLauncher
    {
        public void LaunchDetached(string executable, IReadOnlyList<string> arguments) { }
        public bool WaitForArtifact(string artifactPath, TimeSpan timeout) => false;
    }

    private sealed class NoPatch : IPatchSource
    {
        public void Apply(ChangeCase change) { }
        public void Revert(ChangeCase change) { }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PatchParity.Mcp;
using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Mcp.Tests;

/// <summary>
/// 会话之间的隔离，按服务端真实的组装方式检验：产物目录按会话分开、缓存共享、租约跨进程生效。
/// </summary>
public class SessionIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-session-" + Guid.NewGuid().ToString("N"));

    private const string Csv = "tick,total,c:Item:count,c:Item:hash\n0,1,1,10\n1,2,1,20\n";

    public SessionIsolationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "project"));
        Directory.CreateDirectory(Path.Combine(_root, "repo"));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string[] Args => new[]
    {
        "--unity", Path.Combine(_root, "no-unity.exe"),
        "--project", Path.Combine(_root, "project"),
        "--artifacts", Path.Combine(_root, "artifacts"),
        "--repo", Path.Combine(_root, "repo"),
    };

    // ── 产物目录 ───────────────────────────────────────────

    [Fact]
    public void 每个会话有自己的产物目录而缓存共享()
    {
        using ServerSession first = ServerSession.Create(Args, "s1")!;
        using ServerSession second = ServerSession.Create(Args, "s2")!;

        Assert.NotEqual(first.SessionRoot, second.SessionRoot);
        Assert.Equal(first.CacheRoot, second.CacheRoot);
        Assert.False(first.SessionRoot.StartsWith(first.CacheRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 后一个会话拿不到前一个会话留下的同名运行产物()
    {
        // 第一个用例跑出了 rebuild 与 patched；第二个用例只跑了 rebuild 就去比对。
        // 共用产物目录时会拿第一个用例的 patched 比出一个错误结论，且不报错
        using ServerSession first = ServerSession.Create(Args, "s1")!;
        WriteRun(first, "rebuild");
        WriteRun(first, "patched");
        first.Close();

        using ServerSession second = ServerSession.Create(Args, "s2")!;
        WriteRun(second, "rebuild");

        ToolResult compared = Invoke(second, "compare_runs", new { left = "rebuild", right = "patched" });

        Assert.False(compared.Ok);
        Assert.Contains("找不到运行产物", compared.Text);
    }

    [Fact]
    public void 同一会话内的运行产物照常可比()
    {
        using ServerSession session = ServerSession.Create(Args, "s1")!;
        WriteRun(session, "a");
        WriteRun(session, "b");

        ToolResult compared = Invoke(session, "compare_runs", new { left = "a", right = "b" });

        Assert.True(compared.Ok);
    }

    // ── 租约 ───────────────────────────────────────────────

    [Fact]
    public void 会话关闭后放开工作区()
    {
        using ServerSession first = ServerSession.Create(Args, "s1")!;
        Assert.True(Invoke(first, "revert_patch", new { patch_path = "none.patch" }).Ok);
        using ServerSession second = ServerSession.Create(Args, "s2")!;
        Assert.False(Invoke(second, "revert_patch", new { patch_path = "none.patch" }).Ok);

        first.Close();

        Assert.True(Invoke(second, "revert_patch", new { patch_path = "none.patch" }).Ok);
    }

    [Fact]
    public void 租约跨进程生效且持有进程被杀后自动释放()
    {
        // 真起两个服务端进程。选操作系统的独占句柄而不是记进程号的锁文件，理由就是进程崩溃时系统会放锁——这里杀进程验证它
        using Process first = StartServer();
        using Process second = StartServer();
        try
        {
            JsonNode firstRevert = CallOverStdio(first, "revert_patch", new JsonObject { ["patch_path"] = "none.patch" });
            Assert.False(firstRevert["result"]!["isError"]!.GetValue<bool>());

            JsonNode blocked = CallOverStdio(second, "revert_patch", new JsonObject { ["patch_path"] = "none.patch" });
            Assert.True(blocked["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains($"进程 {first.Id}", blocked["result"]!["content"]![0]!["text"]!.GetValue<string>());

            first.Kill(entireProcessTree: true);
            first.WaitForExit(10_000);

            JsonNode retried = CallOverStdio(second, "revert_patch", new JsonObject { ["patch_path"] = "none.patch" });
            Assert.False(retried["result"]!["isError"]!.GetValue<bool>());
        }
        finally
        {
            foreach (Process server in new[] { first, second })
            {
                if (!server.HasExited)
                {
                    server.Kill(entireProcessTree: true);
                    server.WaitForExit(10_000);
                }
            }
        }
    }

    private static ToolResult Invoke(ServerSession session, string tool, object arguments) =>
        session.Tools.Invoke(tool, JsonSerializer.SerializeToElement(arguments));

    private static void WriteRun(ServerSession session, string name)
    {
        string dir = Path.Combine(session.SessionRoot, "runs", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hashes.csv"), Csv);
    }

    private Process StartServer()
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "patchparity-mcp.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in Args)
        {
            info.ArgumentList.Add(arg);
        }

        Process process = Process.Start(info)!;
        // 标准错误不读会在缓冲区满时卡住服务端
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private static int _requestId;

    private static JsonNode CallOverStdio(Process server, string tool, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _requestId),
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
        };
        server.StandardInput.WriteLine(request.ToJsonString());
        server.StandardInput.Flush();

        Task<string?> line = server.StandardOutput.ReadLineAsync();
        Assert.True(line.Wait(TimeSpan.FromSeconds(30)), "服务端未在 30 秒内应答");
        return JsonNode.Parse(line.Result!)!;
    }
}

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
        // 被测样例仓库须是真的 git 仓库：开工前要核对玩法源码与锁定提交是否一致
        Directory.CreateDirectory(Path.Combine(Project, "Assets", "Brawler"));
        File.WriteAllText(Path.Combine(Project, "Assets", "Brawler", "Item.cs"), "class Item {}\n");
        Git("init", "-q");
        Git("config", "core.autocrlf", "false");
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "base");

        // 一份改动用例：放在仓库外，相当于题目工作树里的补丁
        Directory.CreateDirectory(Path.Combine(_root, "cases"));
        File.WriteAllText(PatchFile,
            "diff --git a/project/Assets/Brawler/Item.cs b/project/Assets/Brawler/Item.cs\n" +
            "--- a/project/Assets/Brawler/Item.cs\n" +
            "+++ b/project/Assets/Brawler/Item.cs\n" +
            "@@ -1 +1 @@\n" +
            "-class Item {}\n" +
            "+class Item { int life = 2; }\n");
    }

    private string PatchFile => Path.Combine(_root, "cases", "x.patch");
    private string ItemSource => File.ReadAllText(Path.Combine(Project, "Assets", "Brawler", "Item.cs"));

    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Repo => Path.Combine(_root, "repo");
    private string Project => Path.Combine(Repo, "project");

    private string[] ArgsWithUnity(string unity) => new[]
    {
        "--unity", unity,
        "--project", Project,
        "--artifacts", Path.Combine(_root, "artifacts"),
        "--repo", Repo,
    };

    private string[] Args => ArgsWithUnity(Path.Combine(_root, "no-unity.exe"));

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

    [Fact]
    public void 上一个会话没收尾就消失时下一个会话开工前恢复源码()
    {
        // 施加改动后会话被强杀：不撤销、不收尾，租约由系统释放
        using (ServerSession killed = ServerSession.Create(Args, "s1")!)
        {
            Invoke(killed, "build_patch", new { patch_path = PatchFile, patch_name = "x" });
            killed.Tools.Invoke("wait_job", JsonSerializer.SerializeToElement(new { job_id = "job-1-build_patch", max_wait_seconds = 10 }));
            Assert.Contains("life = 2", ItemSource);
        }

        using ServerSession next = ServerSession.Create(Args, "s2")!;
        ToolResult started = Invoke(next, "build_package", new { });

        Assert.True(started.Ok, started.Text);
        Assert.Equal("class Item {}\n", ItemSource);
    }

    [Fact]
    public void 客户端只给精简环境时服务端拉起的Unity仍能看到标准环境变量()
    {
        // 云主机实测：经 Pigeon 时 Unity 缺 ProgramFiles(x86)，找不到 MSVC，IL2CPP 构建失败；命令行一路同一台机器上成功
        string seen = Path.Combine(_root, "unity-saw.txt");
        string fakeUnity = Path.Combine(_root, "fake-unity.cmd");
        File.WriteAllText(fakeUnity, "@echo %ProgramFiles(x86)%> \"" + seen + "\"\r\n");

        using Process server = StartServer(ArgsWithUnity(fakeUnity), pigeonEnvironment: true);
        try
        {
            JsonNode started = CallOverStdio(server, "build_package", new JsonObject());
            Assert.False(started["result"]!["isError"]!.GetValue<bool>(), started.ToJsonString());

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(seen) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            Thread.Sleep(300);

            // 变量未定义时 cmd 原样输出 %ProgramFiles(x86)%
            Assert.Equal(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                File.ReadAllText(seen).Trim());
        }
        finally
        {
            server.Kill(entireProcessTree: true);
            server.WaitForExit(10_000);
        }
    }

    [Fact]
    public void 服务端拉起的子进程不握着客户端的管道()
    {
        // 客户端（Pigeon）关闭服务端后要等管道全部关闭才算结束，而且没有第二道超时。
        // 子进程若继承了服务端的标准输出，只要它还活着，客户端就一直等。这里用一个会存活十几秒的假 Unity 检验
        string fakeUnity = Path.Combine(_root, "fake-unity.cmd");
        File.WriteAllText(fakeUnity, "@ping -n 15 127.0.0.1 >nul\r\n");

        using Process server = StartServer(ArgsWithUnity(fakeUnity));
        JsonNode started = CallOverStdio(server, "build_package", new JsonObject());
        Assert.False(started["result"]!["isError"]!.GetValue<bool>(), started.ToJsonString());
        // 等假 Unity 真正起来，再只杀服务端本身、不杀子进程树
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Task<string> rest = server.StandardOutput.ReadToEndAsync();

        var clock = Stopwatch.StartNew();
        server.Kill(entireProcessTree: false);

        Assert.True(rest.Wait(TimeSpan.FromSeconds(8)),
            $"服务端已被杀，但它的标准输出 {clock.Elapsed.TotalSeconds:F0} 秒后仍未关闭：子进程握着管道");
    }

    private static ToolResult Invoke(ServerSession session, string tool, object arguments) =>
        session.Tools.Invoke(tool, JsonSerializer.SerializeToElement(arguments));

    private static void WriteRun(ServerSession session, string name)
    {
        string dir = Path.Combine(session.SessionRoot, "runs", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hashes.csv"), Csv);
    }

    private Process StartServer() => StartServer(Args);

    // Pigeon 透传给 MCP 服务端的环境变量（src/tools/run-command.ts:30-48），其余一律不给
    private static readonly string[] PigeonAllowlist =
    {
        "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "COMSPEC", "TEMP", "TMP", "TMPDIR", "HOME",
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "LANG", "LC_ALL", "LC_CTYPE", "TERM",
    };

    private static Process StartServer(IEnumerable<string> args, bool pigeonEnvironment = false)
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
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        if (pigeonEnvironment)
        {
            foreach (string key in info.Environment.Keys.ToArray())
            {
                if (!PigeonAllowlist.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    info.Environment.Remove(key);
                }
            }
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

    private void Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = Repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

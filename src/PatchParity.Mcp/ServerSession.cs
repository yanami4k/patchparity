using System.Globalization;
using PatchParity.Pipeline;

namespace PatchParity.Mcp;

/// <summary>
/// 一个服务端进程即一个会话。组装工具，并划出本会话的产物目录。
///
/// 产物目录按会话隔离：运行与补丁的名字由调用方起，不同用例的会话很可能都叫 rebuild、patched。
/// 若共用一个目录，后一个用例漏跑某一步时会拿到前一个用例留下的同名产物，比对照常进行、结论却是错的，
/// 且一个接一个地跑就会发生，与并发无关。母包基线缓存则跨会话共享——共享正是缓存的意义。
/// </summary>
public sealed class ServerSession : IDisposable
{
    public required Tools Tools { get; init; }
    public required WorkspaceLease Lease { get; init; }

    /// <summary>本会话的产物目录：补丁、运行、日志都在这里。</summary>
    public required string SessionRoot { get; init; }

    /// <summary>母包基线缓存，跨会话共享。</summary>
    public required string CacheRoot { get; init; }

    public const string Usage = """
        用法: patchparity-mcp --unity <Unity.exe> --project <工程目录>
                              --artifacts <产物目录> --repo <被测样例仓库>
                              [--cache <缓存目录>]
        """;

    /// <summary>会话号：时间加进程号，同一产物根下的会话不会撞名，按名字排序即按时间排序。</summary>
    public static string NewSessionId() =>
        string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");

    /// <summary>参数不全时返回 null。</summary>
    public static ServerSession? Create(IReadOnlyList<string> args, string sessionId)
    {
        Config? config = Config.Parse(args);
        if (config is null)
        {
            return null;
        }

        string sessionRoot = Path.Combine(config.Artifacts, "sessions", sessionId);
        Directory.CreateDirectory(sessionRoot);

        var paths = new ToolPaths
        {
            UnityExecutable = config.Unity,
            ProjectPath = config.Project,
            ArtifactRoot = sessionRoot,
        };

        WorkspaceLease lease = WorkspaceLease.ForProject(config.Project);
        var tools = new Tools(
            paths,
            new UnityInvoker(paths, new ProcessLauncher()),
            JournaledPatchSource.ForProject(config.Repository, config.Project),
            new BaselineCache(config.Cache),
            () => CacheKeyBuilder.ForProject(config.Unity, config.Project),
            lease,
            // 工作目录那份给验证脚本读：pigeon eval 下它是该题的工作树，判分在服务端关闭之后、删工作树之前。
            // 会话产物目录那份留档：工作树判完分就删
            new CaseLedger(
                Path.Combine(Environment.CurrentDirectory, CaseLedger.FileName),
                Path.Combine(sessionRoot, CaseLedger.FileName)));

        return new ServerSession { Tools = tools, Lease = lease, SessionRoot = sessionRoot, CacheRoot = config.Cache };
    }

    /// <summary>
    /// 会话收尾：先兜底撤销本会话施加过的改动，再放开租约。顺序不可颠倒——
    /// 先放租约，别的会话就可能在撤销之前拿到一个仍带着本会话改动的工作区。
    /// </summary>
    public IReadOnlyList<string> Close()
    {
        IReadOnlyList<string> failures = Tools.RevertAll();
        Lease.Dispose();
        return failures;
    }

    public void Dispose() => Lease.Dispose();

    private sealed class Config
    {
        public string Unity = "";
        public string Project = "";
        public string Artifacts = "";
        public string Repository = "";
        public string Cache = "";

        public static Config? Parse(IReadOnlyList<string> args)
        {
            var c = new Config();
            for (int i = 0; i < args.Count - 1; i += 2)
            {
                string value = args[i + 1];
                switch (args[i])
                {
                    case "--unity": c.Unity = value; break;
                    case "--project": c.Project = value; break;
                    case "--artifacts": c.Artifacts = value; break;
                    case "--repo": c.Repository = value; break;
                    case "--cache": c.Cache = value; break;
                    default: return null;
                }
            }

            if (c.Cache.Length == 0 && c.Artifacts.Length > 0)
            {
                c.Cache = Path.Combine(c.Artifacts, "baseline-cache");
            }

            bool complete = c.Unity.Length > 0 && c.Project.Length > 0
                            && c.Artifacts.Length > 0 && c.Repository.Length > 0;
            return complete ? c : null;
        }
    }
}

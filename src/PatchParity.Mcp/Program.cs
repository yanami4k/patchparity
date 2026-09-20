using System.Text;
using System.Text.Json.Nodes;
using PatchParity.Pipeline;

namespace PatchParity.Mcp;

/// <summary>
/// stdio 传输的入口：标准输入每行一条 JSON-RPC 请求，标准输出每行一条应答。
/// 标准输出只允许出现协议报文，任何诊断一律走标准错误，否则会污染报文流。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Config? config = Config.Parse(args);
        if (config is null)
        {
            Console.Error.WriteLine("""
                用法: patchparity-mcp --unity <Unity.exe> --project <工程目录>
                                      --artifacts <产物目录> --repo <被测样例仓库>
                                      [--cache <缓存目录>]
                """);
            return 2;
        }

        var paths = new ToolPaths
        {
            UnityExecutable = config.Unity,
            ProjectPath = config.Project,
            ArtifactRoot = config.Artifacts,
        };
        Directory.CreateDirectory(paths.ArtifactRoot);

        var tools = new Tools(
            paths,
            new UnityInvoker(paths, new ProcessLauncher()),
            new GitPatchSource(config.Repository),
            new BaselineCache(config.Cache),
            () => BuildBaselineKey(config));

        var dispatcher = new McpDispatcher(tools);

        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        while (input.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonObject? response;
            try
            {
                response = dispatcher.Handle(JsonNode.Parse(line));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("处理请求失败: " + e.Message);
                continue;
            }

            if (response is not null)
            {
                output.WriteLine(response.ToJsonString());
            }
        }

        return 0;
    }

    /// <summary>
    /// 母包基线的缓存键。源码取被测工程的 Assets，另加影响构建结果的外部输入。
    /// </summary>
    private static CacheKey BuildBaselineKey(Config config) =>
        CacheKeyBuilder.ForBaseline(
            new[] { Path.Combine(config.Project, "Assets") },
            new Dictionary<string, string>
            {
                ["编辑器可执行文件"] = config.Unity,
                ["工程设置"] = CacheKeyBuilder.HashDirectory(Path.Combine(config.Project, "ProjectSettings")),
                ["包清单"] = File.Exists(Path.Combine(config.Project, "Packages", "manifest.json"))
                    ? File.ReadAllText(Path.Combine(config.Project, "Packages", "manifest.json"))
                    : "缺失",
            });

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

using System.Security.Cryptography;
using System.Text;

namespace PatchParity.Pipeline;

/// <summary>缓存键的一项输入。组成清单随报告打印，便于核对是否有漏项。</summary>
public sealed record CacheKeyInput(string Name, string Digest);

/// <summary>
/// 母包基线的内容哈希缓存键。
///
/// 键必须覆盖全部影响构建结果的输入。漏项的后果是错误复用一个不该复用的母包，
/// 而症状隐蔽——比对照常给出"一致"，却是在错误的基线上得出的。
/// 因此组成清单与键一同保留并打印，宁可键过宽导致多建一次，不可过窄。
/// </summary>
public sealed record CacheKey
{
    public required string Value { get; init; }
    public required IReadOnlyList<CacheKeyInput> Composition { get; init; }

    /// <summary>逐行打印组成，供报告与人工核对。</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("缓存键 ").Append(Value).Append('\n');
        foreach (CacheKeyInput input in Composition)
        {
            sb.Append("  ").Append(input.Name).Append(' ').Append(input.Digest).Append('\n');
        }
        return sb.ToString();
    }
}

public static class CacheKeyBuilder
{
    /// <summary>
    /// 参与构建的源码文件类型。母包由这些内容决定，其一改变则不可复用既有母包。
    /// </summary>
    public static readonly string[] SourcePatterns = { "*.cs", "*.asmdef" };

    /// <summary>
    /// 计算母包基线的缓存键。
    /// sourceRoots 为参与构建的源码目录；extras 为源码之外的输入，
    /// 例如编辑器版本、热更方案版本、脚本后端、脚本宏、构建参数。
    /// </summary>
    public static CacheKey ForBaseline(
        IReadOnlyList<string> sourceRoots,
        IReadOnlyDictionary<string, string> extras)
        => ForBaseline(sourceRoots, Array.Empty<string>(), extras);

    /// <summary>
    /// 构建自己生成、不影响构建结果的目录，相对源码目录。
    /// HybridCLR 每次构建前都重新生成 HybridCLRGenerate（AOT 泛型引用与 link.xml），内容取决于构建那一刻的源码：
    /// 整包重编会留下带着改动的版本，计入键就让母包基线在下一次验证时找不到自己（云主机实测）。
    /// 构建总是先重新生成它，原有内容不影响产物，故不计入。
    /// </summary>
    public static readonly string[] ExcludedFromKey = { "HybridCLRGenerate" };

    /// <param name="excludedDirectories">源码目录下不计入的子目录，相对源码目录。</param>
    public static CacheKey ForBaseline(
        IReadOnlyList<string> sourceRoots,
        IReadOnlyList<string> excludedDirectories,
        IReadOnlyDictionary<string, string> extras)
    {
        var composition = new List<CacheKeyInput>();

        foreach (string root in sourceRoots)
        {
            composition.Add(new CacheKeyInput(
                "源码目录 " + Path.GetFileName(root), HashDirectory(root, excludedDirectories)));
        }

        // 排序后纳入，避免字典枚举顺序影响键
        foreach (KeyValuePair<string, string> extra in extras.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            composition.Add(new CacheKeyInput(extra.Key, Digest(extra.Value)));
        }

        string combined = string.Join("\n", composition.Select(c => c.Name + "=" + c.Digest));
        return new CacheKey { Value = Digest(combined), Composition = composition };
    }

    /// <summary>
    /// 按 Unity 工程的当前状态计算包的缓存键。MCP 与命令行共用这一处，两边的键必须一致才能共享同一份缓存。
    /// 源码取工程的 Assets，另加影响构建结果的外部输入。施加改动前后各算一次即得母包基线与整包重编两个不同的键。
    /// </summary>
    public static CacheKey ForProject(string unityExecutable, string projectPath)
    {
        string manifest = Path.Combine(projectPath, "Packages", "manifest.json");
        return ForBaseline(
            new[] { Path.Combine(projectPath, "Assets") },
            ExcludedFromKey,
            new Dictionary<string, string>
            {
                ["编辑器可执行文件"] = unityExecutable,
                ["工程设置"] = HashDirectory(Path.Combine(projectPath, "ProjectSettings")),
                ["包清单"] = File.Exists(manifest) ? File.ReadAllText(manifest) : "缺失",
            });
    }

    /// <summary>
    /// 目录内容的摘要：按相对路径排序后，逐个纳入路径与文件内容。
    /// 纳入路径是必要的——仅哈希内容时，改名或移动文件不会改变键。
    /// </summary>
    public static string HashDirectory(string root) => HashDirectory(root, Array.Empty<string>());

    /// <param name="excludedDirectories">不计入的子目录，相对 root。</param>
    public static string HashDirectory(string root, IReadOnlyList<string> excludedDirectories)
    {
        if (!Directory.Exists(root))
        {
            return Digest("缺失:" + root);
        }

        var files = new List<string>();
        foreach (string pattern in SourcePatterns)
        {
            files.AddRange(Directory.GetFiles(root, pattern, SearchOption.AllDirectories));
        }

        string[] excluded = excludedDirectories.Select(d => d.Replace('\\', '/').TrimEnd('/') + "/").ToArray();
        var entries = files
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(p => !excluded.Any(d => p.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (string relative in entries)
        {
            sb.Append(relative).Append('\u001f');
            using FileStream stream = File.OpenRead(Path.Combine(root, relative));
            sb.Append(Convert.ToHexString(sha.ComputeHash(stream))).Append('\n');
        }
        return Digest(sb.ToString());
    }

    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

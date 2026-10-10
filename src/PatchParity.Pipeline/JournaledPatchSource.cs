using System.Globalization;
using System.Security.Cryptography;

namespace PatchParity.Pipeline;

/// <summary>
/// 施加改动前先记日志，撤销后再销账；开工前按日志撤销此前会话残留的改动。
///
/// 存在的原因：会话不一定能走完自己的收尾。Pigeon 关闭 MCP 服务端时只等 2 秒就连同子进程强杀，
/// 撞上轮次或墙钟上限时服务端多半正卡在 wait_job 里；进程崩溃、主机重启也一样。
/// 被杀的会话撤销不了改动，工作区租约却随进程由系统释放，下一个会话便在带着上一题改动的源码上构建母包基线——
/// 不报错，只得出错误结论。恢复因此放在开工前而不是收尾时：不依赖上一个会话给自己留多少时间。
///
/// 日志存的是补丁内容的副本而不是原路径：补丁可能放在题目的工作树里，题目结束后工作树会被删掉。
/// 施加与撤销都用副本，同一份改动在日志里只有一条。
/// </summary>
public sealed class JournaledPatchSource : IPatchSource
{
    private readonly IPatchSource _inner;
    private readonly string _patchBaseDir;
    private readonly string _journalDir;
    private readonly Func<IReadOnlyList<string>> _guardedChanges;

    // 本会话施加过的改动：原路径 → 日志条目。原补丁中途被删时仍能撤销，不必再读它的内容
    private readonly Dictionary<string, string> _entryByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="inner">实际施加与撤销改动的实现。</param>
    /// <param name="patchBaseDir">相对路径的补丁以此为基准，须与内层一致——git 以被测样例仓库为工作目录施加补丁。</param>
    /// <param name="journalDir">日志目录。只有持有工作区租约的会话会写它。</param>
    /// <param name="guardedChanges">
    /// 受保护源码相对锁定提交的改动。日志撤销完之后仍有改动，说明来源不明（例如绕过工具直接改了源码），
    /// 此时不猜测如何恢复，拒绝开工。
    /// </param>
    public JournaledPatchSource(
        IPatchSource inner, string patchBaseDir, string journalDir, Func<IReadOnlyList<string>> guardedChanges)
    {
        _inner = inner;
        _patchBaseDir = patchBaseDir;
        _journalDir = journalDir;
        _guardedChanges = guardedChanges;
    }

    /// <summary>
    /// 被测样例的补丁源：git 施加，日志与工作区租约同在工程根目录下。MCP 会话与命令行验证共用这一份日志，
    /// 任一方被中断留下的改动，另一方开工前都能恢复。
    /// 受保护的是玩法源码目录：改动用例只允许触及它（见 cases/README.md），没有用例施加时它必须与锁定提交一致。
    /// </summary>
    public static JournaledPatchSource ForProject(string repository, string projectPath)
    {
        var git = new GitPatchSource(repository);
        string gameplay = Path.Combine(projectPath, "Assets", "Brawler");
        return new JournaledPatchSource(
            git, repository, Path.Combine(projectPath, ".patchparity-journal"), () => git.ChangesUnder(gameplay));
    }

    /// <summary>尚未销账的日志条目，按施加顺序。</summary>
    public IReadOnlyList<string> Entries =>
        Directory.Exists(_journalDir)
            ? Directory.GetFiles(_journalDir)
                .Where(f => f.EndsWith(".patch", StringComparison.Ordinal))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    public void Apply(ChangeCase change)
    {
        // 先记账再施加：两步之间被杀，恢复时反向施加检查不过，只是销掉一条空账
        string entry = Record(Resolve(change.PatchPath));
        _entryByPath[Resolve(change.PatchPath)] = entry;
        _inner.Apply(change with { PatchPath = entry });
    }

    public void Revert(ChangeCase change)
    {
        string? entry = Find(change);
        _inner.Revert(entry is null ? change : change with { PatchPath = entry });
        if (entry is not null)
        {
            File.Delete(entry);
        }
        _entryByPath.Remove(Resolve(change.PatchPath));
    }

    public string? RestoreWorkspace()
    {
        // 后施加的先撤销：两份改动叠在一起时，先撤底下那份会失败
        foreach (string entry in Entries.Reverse())
        {
            try
            {
                _inner.Revert(new ChangeCase { Name = "残留", PatchPath = entry, Expected = ExpectedVerdict.Boundary });
            }
            catch (Exception e)
            {
                return $"撤销残留改动失败（日志 {entry}）：{e.Message}。工作区状态不明，需人工恢复后再开工";
            }
            File.Delete(entry);
        }

        IReadOnlyList<string> unexplained;
        try
        {
            unexplained = _guardedChanges();
        }
        catch (Exception e)
        {
            // 核对不了就当不干净：宁可拒绝开工，也不在状态不明的源码上得出结论
            return "无法核对被测样例的玩法源码：" + e.Message;
        }
        return unexplained.Count == 0
            ? null
            : "被测样例的玩法源码有来源不明的改动，日志里查不到，拒绝开工以免在错误的源码上得出结论：\n  " +
              string.Join("\n  ", unexplained.Take(20));
    }

    private string Record(string patchPath)
    {
        byte[] content = File.ReadAllBytes(patchPath);
        string existing = EntryFor(content);
        if (File.Exists(existing))
        {
            return existing;
        }

        Directory.CreateDirectory(_journalDir);
        // 文件名前缀是记账时刻，按名字排序即施加顺序；后缀是内容摘要，用来认出同一份改动
        string entry = Path.Combine(_journalDir, string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Digest(content)}.patch"));
        string partial = entry + ".partial";
        File.WriteAllBytes(partial, content);
        // 先写临时文件再改名：写到一半被杀不会留下一条内容残缺、却能被当成日志的条目
        File.Move(partial, entry);
        return entry;
    }

    /// <summary>
    /// 找到同一份改动的日志条目：先查本会话的记录，再按内容认。都找不到时交由内层按原路径处理。
    /// </summary>
    private string? Find(ChangeCase change)
    {
        string path = Resolve(change.PatchPath);
        if (_entryByPath.TryGetValue(path, out string? known) && File.Exists(known))
        {
            return known;
        }
        if (!File.Exists(path))
        {
            return null;
        }
        string entry = EntryFor(File.ReadAllBytes(path));
        return File.Exists(entry) ? entry : null;
    }

    private string EntryFor(byte[] content)
    {
        string suffix = "-" + Digest(content) + ".patch";
        return Entries.FirstOrDefault(e => e.EndsWith(suffix, StringComparison.Ordinal))
               ?? Path.Combine(_journalDir, "missing" + suffix);
    }

    private string Resolve(string patchPath) => Path.GetFullPath(patchPath, _patchBaseDir);

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content))[..16].ToLowerInvariant();
}

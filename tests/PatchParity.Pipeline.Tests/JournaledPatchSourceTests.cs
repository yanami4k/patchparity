using System.Diagnostics;
using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Pipeline.Tests;

/// <summary>
/// 改动日志，以真实的 git 仓库检验：会话被强杀后，下一个会话开工前能把工作区恢复干净。
/// </summary>
public class JournaledPatchSourceTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "patchparity-journal-" + Guid.NewGuid().ToString("N"));

    private const string Original = "class Item { int life = 1; }\n";
    private const string Doubled = "class Item { int life = 2; }\n";
    private const string Tripled = "class Item { int life = 3; }\n";

    public JournaledPatchSourceTests()
    {
        Directory.CreateDirectory(Gameplay);
        File.WriteAllText(ItemFile, Original);
        Git("init", "-q");
        Git("config", "core.autocrlf", "false");
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "base");

        // 第一份改动在原始源码上做；第二份在第一份之上做，二者叠加施加
        WritePatch("cases/double.patch", Doubled);
        // git diff 比的是暂存区：先把第一份改动后的状态暂存，第二份才是在它之上的差异
        File.WriteAllText(ItemFile, Doubled);
        Git("add", ItemFile);
        WritePatch("cases/triple.patch", Tripled);
        Git("reset", "-q");
        File.WriteAllText(ItemFile, Original);
    }

    public void Dispose()
    {
        // git 会把对象文件设为只读，删除前先放开
        foreach (string file in Directory.GetFiles(_repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_repo, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Project => Path.Combine(_repo, "Project");
    private string Gameplay => Path.Combine(Project, "Assets", "Brawler");
    private string ItemFile => Path.Combine(Gameplay, "Item.cs");
    private string Item => File.ReadAllText(ItemFile);

    /// <summary>每次新建一个实例，相当于新起一个会话：内存里不带上一个会话的任何记录。</summary>
    private JournaledPatchSource NewSession() => JournaledPatchSource.ForProject(_repo, Project);

    private static ChangeCase Case(string path) => new() { Name = "c", PatchPath = path, Expected = ExpectedVerdict.Safe };

    // ── 记账与销账 ─────────────────────────────────────────

    [Fact]
    public void 施加时记一条日志撤销后销账()
    {
        JournaledPatchSource source = NewSession();

        source.Apply(Case("cases/double.patch"));
        Assert.Equal(Doubled, Item);
        Assert.Single(source.Entries);

        source.Revert(Case("cases/double.patch"));
        Assert.Equal(Original, Item);
        Assert.Empty(source.Entries);
    }

    [Fact]
    public void 同一份改动重复施加只记一条()
    {
        JournaledPatchSource source = NewSession();

        source.Apply(Case("cases/double.patch"));
        source.Apply(Case(Path.Combine(_repo, "cases", "double.patch")));

        Assert.Single(source.Entries);
    }

    [Fact]
    public void 相对路径以被测样例仓库为基准而不是当前目录()
    {
        // MCP 服务端的当前目录是题目的工作树，git 却在被测样例仓库里施加补丁，两边必须对得上
        Assert.NotEqual(Path.GetFullPath(_repo), Path.GetFullPath(Environment.CurrentDirectory));

        NewSession().Apply(Case("cases/double.patch"));

        Assert.Equal(Doubled, Item);
    }

    [Fact]
    public void 原补丁中途被删仍能在本会话内撤销()
    {
        // 补丁可能放在题目的工作树里，工作树随时可能被删
        JournaledPatchSource source = NewSession();
        source.Apply(Case("cases/double.patch"));
        File.Delete(Path.Combine(_repo, "cases", "double.patch"));

        source.Revert(Case("cases/double.patch"));

        Assert.Equal(Original, Item);
        Assert.Empty(source.Entries);
    }

    // ── 开工前恢复 ─────────────────────────────────────────

    [Fact]
    public void 上一个会话被杀后下一个会话开工前撤销残留()
    {
        NewSession().Apply(Case("cases/double.patch"));
        // 上一个会话没有撤销就消失了：Pigeon 2 秒后强杀、崩溃、主机重启都是这样

        string? problem = NewSession().RestoreWorkspace();

        Assert.Null(problem);
        Assert.Equal(Original, Item);
        Assert.Empty(NewSession().Entries);
    }

    [Fact]
    public void 恢复不依赖原补丁文件()
    {
        NewSession().Apply(Case("cases/double.patch"));
        File.Delete(Path.Combine(_repo, "cases", "double.patch"));

        Assert.Null(NewSession().RestoreWorkspace());
        Assert.Equal(Original, Item);
    }

    [Fact]
    public void 叠加的改动按施加的相反顺序撤销()
    {
        JournaledPatchSource killed = NewSession();
        killed.Apply(Case("cases/double.patch"));
        killed.Apply(Case("cases/triple.patch"));
        Assert.Equal(Tripled, Item);

        Assert.Null(NewSession().RestoreWorkspace());

        Assert.Equal(Original, Item);
    }

    [Fact]
    public void 记账后施加前被杀只留下一条空账且能销掉()
    {
        // 施加之前就被杀：日志里有一条，源码却没改。恢复时反向施加检查不过，只销账
        JournaledPatchSource source = NewSession();
        source.Apply(Case("cases/double.patch"));
        Git("checkout", "--", ".");
        Assert.Equal(Original, Item);

        Assert.Null(NewSession().RestoreWorkspace());
        Assert.Empty(NewSession().Entries);
    }

    [Fact]
    public void 写到一半的日志不算条目()
    {
        string journal = Path.Combine(Project, ".patchparity-journal");
        Directory.CreateDirectory(journal);
        File.WriteAllText(Path.Combine(journal, "20260101000000000-abc.patch.partial"), "残缺");

        Assert.Empty(NewSession().Entries);
        Assert.Null(NewSession().RestoreWorkspace());
    }

    [Fact]
    public void 来源不明的改动拒绝开工()
    {
        // 没走工具、直接改了玩法源码：日志里查不到，不猜如何恢复
        File.WriteAllText(ItemFile, "class Item { int life = 9; }\n");

        string? problem = NewSession().RestoreWorkspace();

        Assert.NotNull(problem);
        Assert.Contains("Item.cs", problem);
    }

    [Fact]
    public void 玩法源码里多出的文件也算来源不明()
    {
        File.WriteAllText(Path.Combine(Gameplay, "Extra.cs"), "class Extra {}\n");

        Assert.NotNull(NewSession().RestoreWorkspace());
    }

    [Fact]
    public void 核对不了玩法源码时视为不干净()
    {
        var source = new JournaledPatchSource(
            new GitPatchSource(_repo), _repo, Path.Combine(Project, ".patchparity-journal"),
            () => throw new InvalidOperationException("git 不可用"));

        Assert.Contains("git 不可用", source.RestoreWorkspace());
    }

    private void WritePatch(string relative, string changedContent)
    {
        string before = Item;
        File.WriteAllText(ItemFile, changedContent);
        string diff = Git("diff");
        File.WriteAllText(ItemFile, before);

        string path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, diff);
    }

    private string Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} 失败: {error.Result}");
        return output;
    }
}

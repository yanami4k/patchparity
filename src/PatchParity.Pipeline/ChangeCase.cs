using System.Diagnostics;

namespace PatchParity.Pipeline;

/// <summary>用例的预期结论，写在用例里，用于评分。</summary>
public enum ExpectedVerdict
{
    /// <summary>预期补丁与整包重编等价。</summary>
    Safe,

    /// <summary>预期不等价，或补丁根本跑不起来。</summary>
    Dangerous,

    /// <summary>结论以实测为准，用例里注明实测所得而非主观预期。</summary>
    Boundary,
}

/// <summary>
/// 一个改动用例：一份施加在被测样例玩法源码上的 diff，加上预期结论与覆盖信息。
/// </summary>
public sealed record ChangeCase
{
    public required string Name { get; init; }

    /// <summary>patch 文件路径，相对被测样例仓库根目录施加。</summary>
    public required string PatchPath { get; init; }

    public required ExpectedVerdict Expected { get; init; }

    public Scenario Scenario { get; init; } = new();

    /// <summary>
    /// 改动触及的组件类型名。用于覆盖自检：这些组件在场景中未出现过，
    /// 说明改动点很可能没被执行，此时的"一致"是假阳性。
    /// </summary>
    public IReadOnlyList<string> TouchedComponents { get; init; } = Array.Empty<string>();

    /// <summary>对用例意图的一句话说明，进报告。</summary>
    public string? Note { get; init; }
}

/// <summary>施加与撤销改动。</summary>
public interface IPatchSource
{
    void Apply(ChangeCase change);
    void Revert(ChangeCase change);
}

/// <summary>
/// 以 git 施加与撤销改动。撤销用反向施加而不是丢弃工作区改动，
/// 避免把注入的 harness 源码一并还原掉。
/// </summary>
public sealed class GitPatchSource : IPatchSource
{
    private readonly string _repositoryPath;
    private readonly string _gitExecutable;

    public GitPatchSource(string repositoryPath, string gitExecutable = "git")
    {
        _repositoryPath = repositoryPath;
        _gitExecutable = gitExecutable;
    }

    public void Apply(ChangeCase change)
    {
        if (!IsApplied(change))
        {
            Run("apply", change.PatchPath);
        }
    }

    public void Revert(ChangeCase change)
    {
        if (IsApplied(change))
        {
            Run("apply", "-R", change.PatchPath);
        }
    }

    /// <summary>反向施加能否成功，即为该改动当前是否已施加。</summary>
    private bool IsApplied(ChangeCase change) =>
        TryRun("apply", "-R", "--check", change.PatchPath);

    private void Run(params string[] arguments)
    {
        if (!TryRun(arguments))
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} 失败（工作目录 {_repositoryPath}）");
        }
    }

    private bool TryRun(params string[] arguments)
    {
        var info = new ProcessStartInfo(_gitExecutable)
        {
            WorkingDirectory = _repositoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(info);
        if (process is null)
        {
            return false;
        }
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}

using System.Diagnostics;

namespace PatchParity.Pipeline;

/// <summary>
/// 启动外部进程并等待其产物出现。
///
/// 刻意不以进程退出码或进程存活判完成：实测中 Unity 启动后会换进程继续工作，
/// 最初记录的进程先行退出；批处理命令返回时 Unity 仍在后台持有工程锁。
/// 唯一可靠的完成信号是产物文件。
/// 同理不把构建作为调用方的子进程运行——调用方被超时或内存回收终止时会连带杀掉构建。
/// </summary>
public interface IProcessLauncher
{
    /// <summary>分离启动，立即返回，不等待退出。</summary>
    void LaunchDetached(string executable, IReadOnlyList<string> arguments);

    /// <summary>等待产物文件出现；超时返回 false。</summary>
    bool WaitForArtifact(string artifactPath, TimeSpan timeout);
}

public sealed class ProcessLauncher : IProcessLauncher
{
    private readonly TimeSpan _pollInterval;

    public ProcessLauncher(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public void LaunchDetached(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        Process.Start(info);
    }

    public bool WaitForArtifact(string artifactPath, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(artifactPath))
            {
                return true;
            }
            Thread.Sleep(_pollInterval);
        }
        return File.Exists(artifactPath);
    }
}

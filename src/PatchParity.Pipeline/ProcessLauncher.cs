using System.Collections.Concurrent;
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
    /// <summary>
    /// 分离启动，立即返回，不等待退出。给了 outputLog 时，子进程的标准输出与标准错误写进该文件，
    /// 否则子进程继承调用方的。
    /// </summary>
    void LaunchDetached(string executable, IReadOnlyList<string> arguments, string? outputLog = null);

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

    public void LaunchDetached(string executable, IReadOnlyList<string> arguments, string? outputLog = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = outputLog is not null,
            RedirectStandardError = outputLog is not null,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        // 调用方（如 Pigeon）可能只给了精简过的环境，Unity 缺了标准变量就找不到 MSVC
        ChildEnvironment.FillMissing(info.Environment);
        Process process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 " + executable);
        if (outputLog is not null)
        {
            CopyOutput(process, outputLog);
        }
    }

    // 读完之前进程对象不能被回收：管道随之关闭，子进程再写就会出错
    private static readonly ConcurrentDictionary<Process, byte> Draining = new();

    /// <summary>
    /// 两路输出逐行追加到文件，两路都读到结尾时关闭文件。只靠管道结尾判断，不靠进程退出：
    /// 退出事件可能早于最后几行读完。
    /// </summary>
    private static void CopyOutput(Process process, string outputLog)
    {
        var writer = new StreamWriter(new FileStream(outputLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        var gate = new object();
        int open = 2;
        Draining[process] = 0;

        void OnLine(object? sender, DataReceivedEventArgs e)
        {
            lock (gate)
            {
                if (e.Data is not null)
                {
                    writer.WriteLine(e.Data);
                    writer.Flush();
                    return;
                }
                if (--open == 0)
                {
                    writer.Dispose();
                    Draining.TryRemove(process, out _);
                }
            }
        }

        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
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

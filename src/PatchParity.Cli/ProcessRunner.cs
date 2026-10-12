using System.Diagnostics;
using System.Text;

namespace PatchParity.Cli;

/// <summary>一次要等它结束的子进程：标准输入整段送入，标准输出与标准错误各写一个文件。</summary>
public sealed record ProcessSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? Stdin,
    TimeSpan Timeout,
    string StdoutPath,
    string StderrPath);

/// <summary>超时被结束时 <see cref="ExitCode"/> 无意义，以 <see cref="TimedOut"/> 为准。</summary>
public sealed record ProcessOutcome(int ExitCode, bool TimedOut, string Stdout);

public interface IProcessRunner
{
    ProcessOutcome Run(ProcessSpec spec);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    // 不带 BOM：BOM 会成为题面的第一个字符
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public ProcessOutcome Run(ProcessSpec spec)
    {
        var info = new ProcessStartInfo(spec.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (string argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 " + spec.Executable);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (spec.Stdin is not null)
        {
            process.StandardInput.Write(spec.Stdin);
        }
        process.StandardInput.Close();

        bool exited = process.WaitForExit(spec.Timeout);
        if (!exited)
        {
            // 连同子进程一起结束：Agent 拉起的 MCP 服务端、Unity 不能留下来占着工程
            process.Kill(entireProcessTree: true);
        }
        // 无参的 WaitForExit 会等两路输出读完
        process.WaitForExit();

        string output;
        lock (stdout) output = stdout.ToString();
        File.WriteAllText(spec.StdoutPath, output, Utf8);
        lock (stderr) File.WriteAllText(spec.StderrPath, stderr.ToString(), Utf8);
        return new ProcessOutcome(exited ? process.ExitCode : -1, !exited, output);
    }
}

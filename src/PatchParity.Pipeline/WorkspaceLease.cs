using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PatchParity.Pipeline;

/// <summary>
/// 工作区租约：同一个 Unity 工程同一时刻只允许一个会话施加改动或构建。
///
/// 被测样例的源码状态跨多次工具调用保持——施加改动、编译补丁、整包重编、撤销之间隔着多轮对话，
/// 只给单个任务加锁拦不住另一个会话在中间插进来：它会把带着别人改动的源码当成母包基线，
/// 或把两份改动叠在一起，且都不报错，只得出错误结论。故租约以会话为单位，拿到后持有到进程退出。
///
/// 用操作系统的独占文件句柄实现，不用记录进程号的锁文件：进程崩溃时系统自动释放句柄，不会留下死锁。
/// 锁文件本身留在原地不删，删除与另一个进程的打开之间有竞争。
/// </summary>
public sealed class WorkspaceLease : IDisposable
{
    private readonly string _lockPath;
    private readonly object _gate = new();
    private FileStream? _handle;

    public WorkspaceLease(string lockPath) => _lockPath = lockPath;

    /// <summary>按 Unity 工程目录定位锁文件。同一工程的所有会话（MCP 与命令行）落到同一个文件上。</summary>
    public static WorkspaceLease ForProject(string projectPath) =>
        new(Path.Combine(projectPath, ".patchparity-workspace.lock"));

    public string LockPath => _lockPath;

    public bool Held
    {
        get
        {
            lock (_gate)
            {
                return _handle is not null;
            }
        }
    }

    /// <summary>
    /// 尝试取得租约；已持有时直接返回 true。被其他会话持有时返回 false，并给出持有者的描述。
    /// </summary>
    public bool TryAcquire(out string? holder)
    {
        lock (_gate)
        {
            holder = null;
            if (_handle is not null)
            {
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_lockPath))!);
            try
            {
                // 只放行读：别的会话能读出持有者是谁，但打不开写句柄，即拿不到租约
                _handle = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException)
            {
                holder = ReadHolder();
                return false;
            }

            using Process self = Process.GetCurrentProcess();
            byte[] note = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"进程 {self.Id}（{self.ProcessName}），{DateTime.Now:yyyy-MM-dd HH:mm:ss} 取得"));
            _handle.SetLength(0);
            _handle.Write(note);
            _handle.Flush();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    private string ReadHolder()
    {
        try
        {
            using var stream = new FileStream(_lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string text = reader.ReadToEnd().Trim();
            return text.Length > 0 ? text : "持有者未知";
        }
        catch (IOException)
        {
            return "持有者未知";
        }
    }
}

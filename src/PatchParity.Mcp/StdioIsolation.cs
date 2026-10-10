using System.Runtime.InteropServices;

namespace PatchParity.Mcp;

/// <summary>
/// 让本进程的标准输入、输出、错误不被子进程继承。
///
/// 这三路是 MCP 客户端交给本进程的管道。Windows 上 .NET 启动子进程时会把可继承的句柄一并传下去，
/// 于是 Unity 以及它再拉起的进程（退出后仍常驻一阵的许可证客户端等）都会握着客户端的管道：
/// 客户端关闭服务端时要等管道全部关闭，只要其中一个进程还活着就一直等下去；
/// 子进程若往标准输出写了东西，还会混进协议报文流。Unity 只写日志文件，不需要这三路。
/// </summary>
internal static class StdioIsolation
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x1;

    public static void Apply()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (int which in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
        {
            IntPtr handle = GetStdHandle(which);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                // 失败不致命：最坏情况回到原先的行为
                SetHandleInformation(handle, HandleFlagInherit, 0);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
}

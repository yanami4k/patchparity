namespace PatchParity.Pipeline;

/// <summary>
/// 给启动的 Unity 与验证包补齐 Windows 的标准环境变量。
///
/// MCP 客户端未必把完整环境交给服务端：Pigeon 只透传白名单里的十几个变量（裁决 015），
/// `ProgramFiles(x86)` 等都不在其中。Unity 靠它们找 vswhere 定位 Visual Studio，缺了就报
/// ToolchainNotFoundException，IL2CPP 构建失败（云主机实测：命令行一路构建成功，经 Pigeon 同一台机器上失败）。
/// 这里只补缺失的、不覆盖已有的，值向系统查询而不依赖调用方传了什么，任何客户端下都一样。
/// </summary>
public static class ChildEnvironment
{
    public static void FillMissing(IDictionary<string, string?> environment)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string commonFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        var standard = new (string Name, string Value)[]
        {
            ("ProgramFiles", programFiles),
            ("ProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("ProgramW6432", programFiles),
            ("CommonProgramFiles", commonFiles),
            ("CommonProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)),
            ("CommonProgramW6432", commonFiles),
            ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("ALLUSERSPROFILE", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("SystemDrive", systemDrive),
            ("NUMBER_OF_PROCESSORS", Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("PROCESSOR_ARCHITECTURE", Environment.Is64BitOperatingSystem ? "AMD64" : "x86"),
            ("OS", "Windows_NT"),
            ("USERNAME", Environment.UserName),
            ("USERDOMAIN", Environment.UserDomainName),
            ("COMPUTERNAME", Environment.MachineName),
        };

        // 环境变量名大小写不敏感：调用方给的字典未必用不敏感的比较器，查找时自己比
        var present = new HashSet<string>(
            environment.Where(e => !string.IsNullOrEmpty(e.Value)).Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in standard)
        {
            if (!present.Contains(name) && value.Length > 0)
            {
                environment[name] = value;
            }
        }
    }
}

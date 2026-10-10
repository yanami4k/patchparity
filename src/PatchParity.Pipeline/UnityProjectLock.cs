namespace PatchParity.Pipeline;

/// <summary>
/// Unity 自己的工程锁。编辑器打开工程期间独占 Temp/UnityLockfile；编辑器崩溃后该文件可能残留，
/// 但残留的文件不被任何句柄占用，不妨碍下一个编辑器打开工程。故判"被占用"看能否独占打开，而不看文件在不在。
/// </summary>
public static class UnityProjectLock
{
    public static string LockfilePath(string projectPath) => Path.Combine(projectPath, "Temp", "UnityLockfile");

    public static bool IsHeld(string projectPath)
    {
        string path = LockfilePath(projectPath);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            // 查与开之间编辑器正好退出并删掉了它
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}

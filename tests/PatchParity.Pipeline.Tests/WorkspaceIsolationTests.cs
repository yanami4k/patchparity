using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Pipeline.Tests;

public class WorkspaceIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-isolation-" + Guid.NewGuid().ToString("N"));

    public WorkspaceIsolationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Project => Path.Combine(_root, "project");

    // ── 工作区租约 ─────────────────────────────────────────

    [Fact]
    public void 租约被持有时另一个会话拿不到且能读出持有者()
    {
        using WorkspaceLease first = WorkspaceLease.ForProject(Project);
        using WorkspaceLease second = WorkspaceLease.ForProject(Project);

        Assert.True(first.TryAcquire(out _));
        Assert.False(second.TryAcquire(out string? holder));

        Assert.False(second.Held);
        Assert.Contains($"进程 {Environment.ProcessId}", holder);
    }

    [Fact]
    public void 已持有时再取直接成功()
    {
        using WorkspaceLease lease = WorkspaceLease.ForProject(Project);

        Assert.True(lease.TryAcquire(out _));
        Assert.True(lease.TryAcquire(out _));
        Assert.True(lease.Held);
    }

    [Fact]
    public void 释放后另一个会话能拿到()
    {
        using WorkspaceLease first = WorkspaceLease.ForProject(Project);
        using WorkspaceLease second = WorkspaceLease.ForProject(Project);
        Assert.True(first.TryAcquire(out _));

        first.Dispose();

        Assert.True(second.TryAcquire(out _));
    }

    [Fact]
    public void 不同工程的租约互不影响()
    {
        using WorkspaceLease a = WorkspaceLease.ForProject(Path.Combine(_root, "a"));
        using WorkspaceLease b = WorkspaceLease.ForProject(Path.Combine(_root, "b"));

        Assert.True(a.TryAcquire(out _));
        Assert.True(b.TryAcquire(out _));
    }

    // ── Unity 自己的工程锁 ─────────────────────────────────

    [Fact]
    public void 没有锁文件即未被占用()
    {
        Assert.False(UnityProjectLock.IsHeld(Project));
    }

    [Fact]
    public void 编辑器崩溃残留的锁文件不算占用()
    {
        // 残留的文件没有句柄占着，不妨碍下一个编辑器打开工程；按文件在不在判会把每次崩溃都变成永久阻塞
        WriteLockfile();

        Assert.False(UnityProjectLock.IsHeld(Project));
    }

    [Fact]
    public void 锁文件被独占打开即被占用()
    {
        using FileStream editor = HoldLockfile();

        Assert.True(UnityProjectLock.IsHeld(Project));
    }

    // ── 启动编辑器前的检查 ─────────────────────────────────

    [Fact]
    public void 工程被残留的编辑器占用时立即失败而不启动()
    {
        using FileStream editor = HoldLockfile();
        var launcher = new RecordingLauncher();
        UnityInvoker unity = Invoker(launcher, grace: TimeSpan.Zero);

        StepResult build = unity.BuildPlayer(Path.Combine(_root, "out"), TimeSpan.FromMinutes(30));
        StepResult compile = unity.CompileHotUpdate(Path.Combine(_root, "patch"), TimeSpan.FromMinutes(10));

        Assert.False(build.Succeeded);
        Assert.False(compile.Succeeded);
        Assert.Contains("另一个 Unity 进程占用", build.Failure.Message);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public void 上一个编辑器在宽限期内退出则照常启动()
    {
        // 上一步的编辑器写出产物后还要几秒才退出，紧接着的下一步不应因此失败
        FileStream editor = HoldLockfile();
        var launcher = new RecordingLauncher { OnLaunch = () => Assert.False(UnityProjectLock.IsHeld(Project)) };
        UnityInvoker unity = Invoker(launcher, grace: TimeSpan.FromSeconds(30));
        Task exited = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => editor.Dispose());

        unity.BuildPlayer(Path.Combine(_root, "out"), TimeSpan.FromSeconds(1));
        exited.Wait();

        // 启动发生在上一个编辑器放锁之后，而不是一上来就启动
        Assert.Single(launcher.Launched);
    }

    private UnityInvoker Invoker(IProcessLauncher launcher, TimeSpan grace)
    {
        var paths = new ToolPaths { UnityExecutable = "unity", ProjectPath = Project, ArtifactRoot = _root };
        return new UnityInvoker(paths, launcher) { EditorLockGrace = grace };
    }

    private void WriteLockfile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UnityProjectLock.LockfilePath(Project))!);
        File.WriteAllText(UnityProjectLock.LockfilePath(Project), "");
    }

    /// <summary>模拟打开着工程的编辑器：与实测一致，编辑器运行期间独占该文件。</summary>
    private FileStream HoldLockfile()
    {
        WriteLockfile();
        return new FileStream(UnityProjectLock.LockfilePath(Project), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<string> Launched { get; } = new();

        public Action? OnLaunch { get; init; }

        public void LaunchDetached(string executable, IReadOnlyList<string> arguments)
        {
            OnLaunch?.Invoke();
            Launched.Add(executable);
        }

        public bool WaitForArtifact(string artifactPath, TimeSpan timeout) => false;
    }
}

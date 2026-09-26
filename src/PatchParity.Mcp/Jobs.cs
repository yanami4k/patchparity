namespace PatchParity.Mcp;

/// <summary>一个后台任务的状态快照。</summary>
public sealed record JobSnapshot(string Id, string Tool, bool Completed, TimeSpan Elapsed, ToolResult? Result);

/// <summary>
/// 后台任务登记表。
///
/// 存在的原因：MCP 客户端普遍对单次请求设 60 秒上限，而整包构建要十余分钟，同步阻塞的工具必然超时。
/// 启动 Unity 的工具因此改为"立即返回任务号、另以 wait_job 分次等待"，每次调用都在上限之内结束。
///
/// 会改动源码或 Unity 工程的任务同一时刻只允许一个：同一个 Unity 工程目录同时只能开一个 Unity，
/// 施加改动与读取源码算缓存键也不能交错。撞上即明确拒绝，不排队——排队会让调用方看不出自己在等什么。
/// </summary>
public sealed class Jobs
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _sequence;
    private Entry? _exclusive;

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string Tool { get; init; }
        public required DateTime StartedUtc { get; init; }
        public required Task<ToolResult> Task { get; init; }
    }

    /// <summary>当前占用工程的任务号；无则为 null。</summary>
    public string? RunningExclusive
    {
        get
        {
            lock (_gate)
            {
                return _exclusive is { Task.IsCompleted: false } ? _exclusive.Id : null;
            }
        }
    }

    /// <summary>
    /// 启动一个独占任务。已有独占任务在运行时不启动，返回 null 并给出正在运行的任务号。
    /// </summary>
    public string? TryStartExclusive(string tool, Func<ToolResult> work, out string? blockedBy)
    {
        lock (_gate)
        {
            if (_exclusive is { Task.IsCompleted: false })
            {
                blockedBy = _exclusive.Id;
                return null;
            }

            var entry = Start(tool, work);
            _exclusive = entry;
            blockedBy = null;
            return entry.Id;
        }
    }

    /// <summary>
    /// 至多等待 maxWait；完成则返回结果，否则返回仍在运行的快照。未知任务号返回 null。
    /// </summary>
    public JobSnapshot? Wait(string id, TimeSpan maxWait)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(id, out entry);
        }
        if (entry is null)
        {
            return null;
        }

        bool completed = entry.Task.Wait(maxWait);
        TimeSpan elapsed = DateTime.UtcNow - entry.StartedUtc;
        return new JobSnapshot(entry.Id, entry.Tool, completed, elapsed, completed ? entry.Task.Result : null);
    }

    /// <summary>等所有未完成的任务结束，供会话收尾在撤销改动之前调用。</summary>
    public void WaitAll(TimeSpan timeout)
    {
        Task[] pending;
        lock (_gate)
        {
            pending = _entries.Values.Select(e => (Task)e.Task).Where(t => !t.IsCompleted).ToArray();
        }
        if (pending.Length > 0)
        {
            Task.WaitAll(pending, timeout);
        }
    }

    private Entry Start(string tool, Func<ToolResult> work)
    {
        string id = $"job-{++_sequence}-{tool}";
        var task = Task.Run(() =>
        {
            try
            {
                return work();
            }
            catch (Exception e)
            {
                // 后台异常照样作为工具失败交回，调用方经 wait_job 能看到原因
                return ToolResult.Failure(e.GetType().Name + ": " + e.Message);
            }
        });
        var entry = new Entry { Id = id, Tool = tool, StartedUtc = DateTime.UtcNow, Task = task };
        _entries[id] = entry;
        return entry;
    }
}

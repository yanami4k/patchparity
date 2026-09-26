namespace PatchParity.Pipeline;

/// <summary>命中缓存时的结果。</summary>
public sealed record CacheLookup(bool Hit, string PackageDirectory, CacheKey Key);

/// <summary>
/// 母包基线的缓存。键为内容哈希，值为一整个已构建好的母包目录。
///
/// 只缓存母包基线：它对全部用例是同一个，建一次即可反复使用。
/// 整包重编不进缓存——那是每个用例各自的标准答案，本就不可共享。
/// </summary>
public sealed class BaselineCache
{
    private readonly string _root;

    public BaselineCache(string root) => _root = root;

    /// <summary>缓存中该键对应的目录，无论是否已存在。</summary>
    public string DirectoryFor(CacheKey key) => DirectoryFor(key.Value);

    /// <summary>按键值定位。调用方只持有键值字符串时使用，例如跨工具调用传递的包标识。</summary>
    public string DirectoryFor(string keyValue)
    {
        // 键值来自调用方，拒绝任何能跳出缓存根目录的写法
        if (keyValue.Length == 0 || keyValue.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            keyValue.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("非法的包标识: " + keyValue);
        }
        return Path.Combine(_root, keyValue);
    }

    /// <summary>按键值判断该包是否已完整构建。</summary>
    public bool IsComplete(string keyValue) => File.Exists(MarkerPath(DirectoryFor(keyValue)));

    /// <summary>
    /// 查缓存。以完成标记文件判命中，而不是以目录存在判——
    /// 构建中途失败会留下一个不完整的目录，按目录存在判会把它当成可用的母包。
    /// </summary>
    public CacheLookup Lookup(CacheKey key)
    {
        string directory = DirectoryFor(key);
        return new CacheLookup(File.Exists(MarkerPath(directory)), directory, key);
    }

    /// <summary>构建成功后标记该目录可用，并留下缓存键的组成以便核对。</summary>
    public void MarkComplete(CacheKey key)
    {
        string directory = DirectoryFor(key);
        Directory.CreateDirectory(directory);
        File.WriteAllText(MarkerPath(directory), key.Describe());
    }

    /// <summary>丢弃一条缓存，用于构建失败后的清理。</summary>
    public void Invalidate(CacheKey key)
    {
        string marker = MarkerPath(DirectoryFor(key));
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }

    private static string MarkerPath(string directory) => Path.Combine(directory, "cache-complete.txt");
}

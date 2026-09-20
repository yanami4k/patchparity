using Xunit;
using PatchParity.Pipeline;

namespace PatchParity.Pipeline.Tests;

public class CacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchparity-cache-" + Guid.NewGuid().ToString("N"));

    public CacheTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_root, "src", "B.asmdef"), "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private string SourceDir => Path.Combine(_root, "src");

    private static readonly Dictionary<string, string> Extras = new()
    {
        ["Unity 版本"] = "6000.3.18f1",
        ["HybridCLR 版本"] = "v8.14.1",
    };

    private CacheKey Key(IReadOnlyDictionary<string, string>? extras = null) =>
        CacheKeyBuilder.ForBaseline(new[] { SourceDir }, extras ?? Extras);

    [Fact]
    public void 输入不变则键不变()
    {
        Assert.Equal(Key().Value, Key().Value);
    }

    [Fact]
    public void 源码内容改变则键改变()
    {
        string before = Key().Value;
        File.WriteAllText(Path.Combine(SourceDir, "A.cs"), "class A { int x; }");

        Assert.NotEqual(before, Key().Value);
    }

    [Fact]
    public void 新增源码文件则键改变()
    {
        string before = Key().Value;
        File.WriteAllText(Path.Combine(SourceDir, "C.cs"), "class C {}");

        Assert.NotEqual(before, Key().Value);
    }

    [Fact]
    public void 仅改名也会改变键()
    {
        // 只哈希内容时改名不会改变键，那会让"把逻辑挪进另一个文件或程序集"这类改动错误命中缓存。
        // 改名后的排序位置必须与改名前相同，否则文件顺序变化本身就会改变键，
        // 这条断言就会因为错误的理由通过，测不到路径是否真的纳入了键。
        File.WriteAllText(Path.Combine(SourceDir, "A2.cs"), "class A2 {}");
        string before = Key().Value;

        File.Move(Path.Combine(SourceDir, "A2.cs"), Path.Combine(SourceDir, "A3.cs"));

        Assert.NotEqual(before, Key().Value);
    }

    [Fact]
    public void 编辑器版本改变则键改变()
    {
        string before = Key().Value;
        var other = new Dictionary<string, string>(Extras) { ["Unity 版本"] = "6000.3.19f1" };

        Assert.NotEqual(before, Key(other).Value);
    }

    [Fact]
    public void 键的组成清单可打印且含每一项输入()
    {
        CacheKey key = Key();
        string text = key.Describe();

        Assert.Contains("Unity 版本", text);
        Assert.Contains("HybridCLR 版本", text);
        Assert.Contains("源码目录 src", text);
        Assert.Equal(3, key.Composition.Count);
    }

    [Fact]
    public void 未构建过时不命中()
    {
        var cache = new BaselineCache(Path.Combine(_root, "cache"));

        Assert.False(cache.Lookup(Key()).Hit);
    }

    [Fact]
    public void 标记完成后命中且目录稳定()
    {
        var cache = new BaselineCache(Path.Combine(_root, "cache"));
        CacheKey key = Key();
        cache.MarkComplete(key);

        CacheLookup lookup = cache.Lookup(key);

        Assert.True(lookup.Hit);
        Assert.Equal(cache.DirectoryFor(key), lookup.PackageDirectory);
    }

    [Fact]
    public void 目录存在但未标记完成时不命中()
    {
        // 构建中途失败会留下不完整的目录，按目录存在判会把它当成可用的母包
        var cache = new BaselineCache(Path.Combine(_root, "cache"));
        CacheKey key = Key();
        Directory.CreateDirectory(cache.DirectoryFor(key));
        File.WriteAllText(Path.Combine(cache.DirectoryFor(key), "半成品.txt"), "");

        Assert.False(cache.Lookup(key).Hit);
    }

    [Fact]
    public void 撤销标记后不再命中()
    {
        var cache = new BaselineCache(Path.Combine(_root, "cache"));
        CacheKey key = Key();
        cache.MarkComplete(key);
        cache.Invalidate(key);

        Assert.False(cache.Lookup(key).Hit);
    }

    [Fact]
    public void 不同键落在不同目录()
    {
        var cache = new BaselineCache(Path.Combine(_root, "cache"));
        CacheKey first = Key();
        var other = new Dictionary<string, string>(Extras) { ["Unity 版本"] = "6000.3.19f1" };

        Assert.NotEqual(cache.DirectoryFor(first), cache.DirectoryFor(Key(other)));
    }
}

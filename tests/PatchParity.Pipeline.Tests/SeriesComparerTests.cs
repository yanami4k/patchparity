using Xunit;
using PatchParity.Pipeline;

namespace PatchParity.Pipeline.Tests;

public class SeriesComparerTests
{
    // 表头形状取自实测产出：tick,total,frame 之后是每个组件类型的计数与哈希
    private const string Header =
        "tick,total,frame,c:TransformComponent:count,c:TransformComponent:hash,c:ItemComponent:count,c:ItemComponent:hash";

    private static HashSeries Series(params string[] rows) =>
        HashSeries.Parse(Header + "\n" + string.Join("\n", rows) + "\n");

    [Fact]
    public void 逐帧相同时判为一致()
    {
        var a = Series("0,100,900,4,11,1,21", "1,101,901,4,12,1,22");
        var b = Series("0,100,900,4,11,1,21", "1,101,901,4,12,1,22");

        ComparisonResult result = SeriesComparer.Compare(a, b);

        Assert.True(result.Identical);
        Assert.Null(result.FirstDivergentTick);
    }

    [Fact]
    public void 报出首个总哈希不同的tick而不是最后一个()
    {
        var a = Series("0,100,900,4,11,1,21", "1,101,901,4,12,1,22", "2,102,902,4,13,1,23");
        var b = Series("0,100,900,4,11,1,21", "1,999,901,4,12,1,99", "2,998,902,4,13,1,98");

        ComparisonResult result = SeriesComparer.Compare(a, b);

        Assert.False(result.Identical);
        Assert.Equal(1, result.FirstDivergentTick);
    }

    [Fact]
    public void 分歧时只报取值不同的分层()
    {
        // 实测中"道具寿命递减加速"这一改动的表现：唯一分歧层就是被改的组件
        var a = Series("0,100,900,4,11,1,21", "1,101,901,4,12,1,22");
        var b = Series("0,100,900,4,11,1,21", "1,199,901,4,12,1,77");

        ComparisonResult result = SeriesComparer.Compare(a, b);

        LayerDifference only = Assert.Single(result.DivergentLayers);
        Assert.Equal("c:ItemComponent:hash", only.Layer);
        Assert.Equal("22", only.Left);
        Assert.Equal("77", only.Right);
    }

    [Fact]
    public void 总哈希不同而分层全同时标记为分层未覆盖()
    {
        var a = Series("0,100,900,4,11,1,21");
        var b = Series("0,555,900,4,11,1,21");

        ComparisonResult result = SeriesComparer.Compare(a, b);

        Assert.False(result.Identical);
        Assert.Empty(result.DivergentLayers);
        Assert.True(result.DivergedOutsideLayers);
    }

    [Fact]
    public void 列按名字对齐而不按位置()
    {
        // 两次构建注册的组件集合不同，共有列的顺序也不同；按位置对齐会把不同的层错配
        var a = HashSeries.Parse(
            "tick,total,c:A:count,c:A:hash,c:B:count,c:B:hash\n0,100,1,11,2,22\n");
        var b = HashSeries.Parse(
            "tick,total,c:B:count,c:B:hash,c:A:count,c:A:hash\n0,100,2,22,1,11\n");

        ComparisonResult result = SeriesComparer.Compare(a, b);

        Assert.True(result.Identical);
        Assert.Empty(result.LeftOnlyColumns);
        Assert.Empty(result.RightOnlyColumns);
    }

    [Fact]
    public void 报出只在一侧出现的列()
    {
        var a = HashSeries.Parse("tick,total,c:A:count,c:A:hash\n0,100,1,11\n");
        var b = HashSeries.Parse("tick,total,c:B:count,c:B:hash\n0,100,1,11\n");

        ComparisonResult result = SeriesComparer.Compare(a, b);

        Assert.Contains("c:A:count", result.LeftOnlyColumns);
        Assert.Contains("c:B:hash", result.RightOnlyColumns);
    }

    [Fact]
    public void 帧数不等不算一致()
    {
        var a = Series("0,100,900,4,11,1,21", "1,101,901,4,12,1,22");
        var b = Series("0,100,900,4,11,1,21");

        ComparisonResult result = SeriesComparer.Compare(a, b);

        Assert.False(result.Identical);
        Assert.Equal(2, result.LeftCount);
        Assert.Equal(1, result.RightCount);
    }

    [Fact]
    public void 表头重复列名当场失败()
    {
        Assert.Throws<FormatException>(() =>
            HashSeries.Parse("tick,total,c:A:hash,c:A:hash\n0,100,1,1\n"));
    }

    [Fact]
    public void 字段数与表头不符当场失败()
    {
        Assert.Throws<FormatException>(() => HashSeries.Parse("tick,total\n0,100,999\n"));
    }
}

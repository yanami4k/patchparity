using Xunit;
using PatchParity.Pipeline;

namespace PatchParity.Pipeline.Tests;

public class PreconditionsTests
{
    private const string Header =
        "tick,total,frame,c:TransformComponent:count,c:TransformComponent:hash,c:ItemComponent:count,c:ItemComponent:hash";

    private static HashSeries Series(params string[] rows) =>
        HashSeries.Parse(Header + "\n" + string.Join("\n", rows) + "\n");

    [Fact]
    public void 两次运行一致则确定性自检通过()
    {
        var first = Series("0,100,900,4,11,1,21", "1,101,901,4,12,1,22");
        var second = Series("0,100,900,4,11,1,21", "1,101,901,4,12,1,22");

        DeterminismCheck check = Preconditions.CheckDeterminism(first, second);

        Assert.True(check.Passed);
    }

    [Fact]
    public void 两次运行不同则确定性自检失败并给出分歧位置()
    {
        // 实测形态：世界种子取自墙上时钟时，第 0 帧的种子组件就不同
        var first = Series("0,100,900,4,11,1,21");
        var second = Series("0,200,900,4,77,1,21");

        DeterminismCheck check = Preconditions.CheckDeterminism(first, second);

        Assert.False(check.Passed);
        Assert.Equal(0, check.FirstDivergentTick);
        Assert.Contains(check.DivergentLayers, d => d.Layer == "c:TransformComponent:hash");
    }

    [Fact]
    public void 空世界的覆盖自检不通过()
    {
        // 全部组件计数为 0：两路必然一致，但这种一致不构成补丁等价的证据
        var series = Series("0,100,900,0,0,0,0", "1,101,901,0,0,0,0");

        CoverageCheck check = Preconditions.CheckCoverage(series, Array.Empty<string>());

        Assert.False(check.Passed);
        Assert.Equal(0, check.LiveComponents);
        Assert.NotNull(check.Reason);
    }

    [Fact]
    public void 世界非空但未指定触及组件时覆盖强度只算最低档()
    {
        var series = Series("0,100,900,4,11,1,21");

        CoverageCheck check = Preconditions.CheckCoverage(series, Array.Empty<string>());

        Assert.True(check.Passed);
        Assert.Equal(CoverageStrength.WorldNotEmpty, check.Strength);
    }

    [Fact]
    public void 改动触及的组件在场景中出现过才算覆盖()
    {
        var series = Series("0,100,900,4,11,1,21");

        CoverageCheck check = Preconditions.CheckCoverage(series, new[] { "ItemComponent" });

        Assert.True(check.Passed);
        Assert.Equal(CoverageStrength.TouchedComponentsPresent, check.Strength);
    }

    [Fact]
    public void 改动触及的组件全程不出现则覆盖自检失败()
    {
        var series = Series("0,100,900,4,11,0,0", "1,101,901,4,12,0,0");

        CoverageCheck check = Preconditions.CheckCoverage(series, new[] { "ItemComponent" });

        Assert.False(check.Passed);
        Assert.Contains("ItemComponent", check.MissingComponents);
    }

    [Fact]
    public void 组件在中途出现过即算覆盖()
    {
        var series = Series("0,100,900,4,11,0,0", "1,101,901,4,12,1,22", "2,102,902,4,13,0,0");

        CoverageCheck check = Preconditions.CheckCoverage(series, new[] { "ItemComponent" });

        Assert.True(check.Passed);
    }
}

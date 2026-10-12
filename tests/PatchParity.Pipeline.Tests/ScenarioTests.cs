using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Pipeline.Tests;

/// <summary>场景的规范化与校验（裁决 019）。场景是验证的输入，选项串即其身份。</summary>
public class ScenarioTests
{
    [Fact]
    public void 缺省场景的选项串与加入场景生成参数之前相同()
    {
        // 已有用例的实测结论都是在这个选项串下得出的，一个字也不能变
        Assert.Equal("seed=42;ticks=600;players=2;bots=0;maxEntities=512;deltaTimeMs=25", new Scenario().ToOptionString());
        Assert.Equal("seed42-t600-p2-b0", new Scenario().Name);
        Assert.False(new Scenario().IsCustomized);
    }

    [Fact]
    public void 解析后按规范顺序输出且参数相同即选项串相同()
    {
        Scenario a = Scenario.Parse("chase=1;moveSpeed=5;ground=1;spawn=1:0,-1.5:0;attack=400;players=2");
        Scenario b = Scenario.Parse("players=2;ground=1;moveSpeed=5.00;spawn=1:0,-1.5:0;attack=400;chase=1");

        Assert.Equal(a.ToOptionString(), b.ToOptionString());
        Assert.Equal(
            "seed=42;ticks=600;players=2;bots=0;maxEntities=512;deltaTimeMs=25;ground=1;moveSpeed=5;spawn=1:0,-1.5:0;attack=400;chase=1",
            a.ToOptionString());
        Assert.Equal(a.Name, b.Name);
        Assert.NotEqual(new Scenario().Name, a.Name);
    }

    [Fact]
    public void 只给缺省值以内的参数时与缺省场景同一选项串()
    {
        Assert.Equal(new Scenario().ToOptionString(), Scenario.Parse("seed=42;ticks=600").ToOptionString());
    }

    [Theory]
    [InlineData("speed=5", "未知的场景参数")]
    [InlineData("ticks=0", "ticks")]
    [InlineData("ticks=6001", "ticks")]
    [InlineData("players=5", "players")]
    [InlineData("moveSpeed=21", "moveSpeed")]
    [InlineData("moveSpeed=1.234", "moveSpeed")]
    [InlineData("classes=0,4", "classes")]
    [InlineData("classes=0", "须等于 players")]
    [InlineData("spawn=0:0", "须等于 players")]
    [InlineData("spawn=0:0,19:0", "spawn")]
    [InlineData("spawn=0:0,1.25:0", "spawn")]
    [InlineData("spawn=0,0", "x:z")]
    [InlineData("attack=1001", "attack")]
    [InlineData("ground=2", "ground")]
    [InlineData("ground=1;ground=1", "重复")]
    [InlineData("ground", "k=v")]
    public void 越界或不认识的参数被拒绝并说明原因(string options, string expected)
    {
        var error = Assert.Throws<FormatException>(() => Scenario.Parse(options));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void 职业与出生位置的个数随玩家数校验()
    {
        Scenario s = Scenario.Parse("players=3;classes=0,1,2;spawn=0:0,1:1,-2:2");

        Assert.Equal(new[] { 0, 1, 2 }, s.Classes);
        Assert.Equal(3, s.Spawn!.Count);
    }
}

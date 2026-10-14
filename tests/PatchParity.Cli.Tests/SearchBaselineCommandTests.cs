using System.Text.Json.Nodes;
using PatchParity.Cli;
using PatchParity.Pipeline;
using Xunit;

namespace PatchParity.Cli.Tests;

/// <summary>search-baseline 子命令：随机搜索基线的汇总与参数。搜索本身见核心库的 RandomSearchTests。</summary>
public class SearchBaselineCommandTests
{
    [Fact]
    public void 汇总只在命中的轮次上平均并单列命中率()
    {
        var trials = new[]
        {
            new SearchTrial(1, 2, new[] { "a", "b" }, 0),
            new SearchTrial(2, null, new[] { "a", "b", "c" }, 1),
            new SearchTrial(3, 5, new[] { "a", "b", "c", "d", "e" }, 0),
        };

        JsonObject s = SearchBaselineCommand.Summarize("search-a", 8, trials);

        Assert.Equal(2, (int)s["hits"]!);
        Assert.Equal(0.667, (double)s["hit_rate"]!);
        Assert.Equal(3.5, (double)s["mean_hit_attempt"]!);
        Assert.Equal(new int?[] { 2, null, 5 }, s["attempts"]!.AsArray().Select(n => (int?)n));
        Assert.Equal(1, (int)s["failed_runs"]!);
    }

    [Fact]
    public void 一次都没命中时平均为空()
    {
        JsonObject s = SearchBaselineCommand.Summarize("x", 8, new[] { new SearchTrial(1, null, Array.Empty<string>(), 0) });

        Assert.Equal(0, (int)s["hits"]!);
        Assert.Null(s["mean_hit_attempt"]);
    }

    [Theory]
    [InlineData("search-baseline")]
    [InlineData("search-baseline|--case|x.patch")]
    [InlineData("search-baseline|--unity|u|--project|p|--repo|r|--artifacts|a|--cache|c|--case|x|--name|n|--trials|0")]
    [InlineData("search-baseline|--unity|u|--project|p|--repo|r|--artifacts|a|--cache|c|--case|x|--name|n|--bogus|1")]
    public void 参数不全或不合法即为用法错误(string args)
    {
        Assert.Equal(2, SearchBaselineCommand.Run(args.Split('|')));
    }
}

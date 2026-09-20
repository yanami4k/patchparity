using Xunit;
using PatchParity.Pipeline;

namespace PatchParity.Pipeline.Tests;

/// <summary>
/// 输入取自本工程实测遇到的原始失败文本，不是构造的样例。
/// </summary>
public class FailureClassifierTests
{
    [Fact]
    public void 成功的运行不产生失败报告()
    {
        FailureReport report = FailureClassifier.ClassifyRun(
            "OK ticks=600 seed=42 players=2 bots=0 lastHash=6375140586867292485 lastLiveComponents=26");

        Assert.Equal(FailureStage.None, report.Stage);
    }

    [Fact]
    public void 识别以热更类型实例化的AOT泛型()
    {
        FailureReport report = FailureClassifier.ClassifyRun(
            "FAIL MissingMethodException: AOT generic method not instantiated in aot. " +
            "assembly:xpTURN.Klotho.Runtime.dll, method:System.Void xpTURN.Klotho.ECS.Frame::Add<Brawler.GameTimerStateComponent>(EntityRef,GameTimerStateComponent)");

        Assert.Equal(KnownFault.AotGenericNotInstantiated, report.Fault);
        Assert.Equal(FailureStage.Runtime, report.Stage);
        Assert.Equal("MissingMethodException", report.ExceptionType);
    }

    [Fact]
    public void 识别母包已裁剪的类型()
    {
        FailureReport report = FailureClassifier.ClassifyRun(
            "FAIL TypeLoadException: Could not load type 'Brawler.GameMenu' from assembly 'Brawler.View'.");

        Assert.Equal(KnownFault.StrippedTypeMissing, report.Fault);
        Assert.Equal(FailureStage.Load, report.Stage);
    }

    [Fact]
    public void 识别世界初始化缺步骤()
    {
        FailureReport missingAsset = FailureClassifier.ClassifyRun(
            "FAIL KeyNotFoundException: DataAsset not found: id=1001, type=BrawlerGameRulesAsset");
        FailureReport missingRoot = FailureClassifier.ClassifyRun(
            "FAIL ArgumentException: [HFSMManager] Unknown rootId: 1. Call HFSMRoot.Register() before Init().");

        Assert.Equal(KnownFault.WorldSetupIncomplete, missingAsset.Fault);
        Assert.Equal(KnownFault.WorldSetupIncomplete, missingRoot.Fault);
    }

    [Fact]
    public void 识别AOT程序集引用热更程序集导致的构建失败()
    {
        FailureReport report = FailureClassifier.ClassifyBuild(
            "GenerateStripedAOTDlls failed\nFailed to resolve assembly: 'Brawler.ECS'\n");

        Assert.Equal(FailureStage.Build, report.Stage);
        Assert.Equal(KnownFault.AotReferencesHotUpdate, report.Fault);
    }

    [Fact]
    public void 未知失败仍给出阶段与原文()
    {
        FailureReport report = FailureClassifier.ClassifyRun("FAIL InvalidOperationException: 某种未见过的故障");

        Assert.Equal(KnownFault.Unknown, report.Fault);
        Assert.Equal(FailureStage.Runtime, report.Stage);
        Assert.Equal("某种未见过的故障", report.Message);
    }

    [Fact]
    public void 构建日志无已知模式时不报失败()
    {
        FailureReport report = FailureClassifier.ClassifyBuild("Build succeeded\n");

        Assert.Equal(FailureStage.None, report.Stage);
    }
}

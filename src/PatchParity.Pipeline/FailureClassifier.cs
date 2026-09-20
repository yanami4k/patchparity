using System.Text.RegularExpressions;

namespace PatchParity.Pipeline;

/// <summary>
/// 验证在哪一步失败。分档而不是只报成功与失败，因为失败点随场景推进而后移，
/// 跑到第几步本身就是信息：装载失败说明补丁根本没跑起来，运行期第 k 帧失败说明前 k-1 帧的路径是通的。
/// </summary>
public enum FailureStage
{
    /// <summary>没有失败。</summary>
    None,

    /// <summary>构建或编译阶段失败，补丁未能产出。</summary>
    Build,

    /// <summary>母包启动后加载热更程序集或补充 AOT 元数据时失败。</summary>
    Load,

    /// <summary>模拟运行期失败，通常是第一次执行到某处时才暴露。</summary>
    Runtime,

    /// <summary>两路都跑完，但逐 tick 哈希不一致。</summary>
    Mismatch,
}

public sealed record FailureReport
{
    public required FailureStage Stage { get; init; }

    /// <summary>失败的异常类型名，取自运行结果的首行。</summary>
    public string? ExceptionType { get; init; }

    public string? Message { get; init; }

    /// <summary>已知故障模式的归类，便于 Eval 题集按类统计。</summary>
    public KnownFault Fault { get; init; } = KnownFault.Unknown;
}

/// <summary>
/// 已在本工程实测遇到过的故障模式。它们的共同点是构建成功却跑不通，静态检查覆盖不到。
/// </summary>
public enum KnownFault
{
    Unknown,

    /// <summary>母包中的泛型以热更类型实例化，母包内无对应代码；需补充 AOT 元数据。</summary>
    AotGenericNotInstantiated,

    /// <summary>加载的程序集依赖的类型已被母包裁剪掉。</summary>
    StrippedTypeMissing,

    /// <summary>托管代码裁剪阶段无法解析热更程序集，通常因 AOT 程序集引用了热更程序集。</summary>
    AotReferencesHotUpdate,

    /// <summary>世界初始化缺步骤，例如系统或数据资产未注册。</summary>
    WorldSetupIncomplete,
}

public static class FailureClassifier
{
    // 母包启动器把失败写成 "FAIL <异常类型>: <消息>"
    private static readonly Regex FailLine = new(@"^FAIL\s+(?<type>\w+):\s*(?<message>.*)$", RegexOptions.Compiled);

    /// <summary>
    /// 依据运行结果文件的首行判断失败档位。resultLine 为空或以 OK 开头表示这一步成功。
    /// </summary>
    public static FailureReport ClassifyRun(string? resultLine)
    {
        if (string.IsNullOrWhiteSpace(resultLine) || resultLine.StartsWith("OK", StringComparison.Ordinal))
        {
            return new FailureReport { Stage = FailureStage.None };
        }

        Match match = FailLine.Match(resultLine.Trim());
        string type = match.Success ? match.Groups["type"].Value : "Unknown";
        string message = match.Success ? match.Groups["message"].Value : resultLine.Trim();

        KnownFault fault = ClassifyFault(type, message);
        FailureStage stage = fault switch
        {
            // 补充元数据与加载程序集都发生在驱动模拟之前
            KnownFault.AotGenericNotInstantiated => FailureStage.Runtime,
            KnownFault.StrippedTypeMissing => FailureStage.Load,
            KnownFault.WorldSetupIncomplete => FailureStage.Runtime,
            _ => FailureStage.Runtime,
        };

        return new FailureReport
        {
            Stage = stage,
            ExceptionType = type,
            Message = message,
            Fault = fault,
        };
    }

    /// <summary>依据构建日志判断构建期失败。</summary>
    public static FailureReport ClassifyBuild(string buildLog)
    {
        // 两个特征都指向同一根因：裁剪阶段解析不到热更程序集。
        // 前者出现在 Unity 日志里，后者出现在构建脚本写出的报告里，后者更早可得。
        if (buildLog.Contains("Failed to resolve assembly", StringComparison.Ordinal) ||
            buildLog.Contains("GenerateStripedAOTDlls failed", StringComparison.Ordinal))
        {
            return new FailureReport
            {
                Stage = FailureStage.Build,
                Message = "托管代码裁剪阶段无法解析热更程序集",
                Fault = KnownFault.AotReferencesHotUpdate,
            };
        }
        if (buildLog.Contains("Scripts have compiler errors", StringComparison.Ordinal))
        {
            return new FailureReport { Stage = FailureStage.Build, Message = "脚本编译失败" };
        }
        return new FailureReport { Stage = FailureStage.None };
    }

    private static KnownFault ClassifyFault(string type, string message)
    {
        if (message.Contains("AOT generic method not instantiated", StringComparison.Ordinal))
        {
            return KnownFault.AotGenericNotInstantiated;
        }
        if (type == "TypeLoadException" || message.Contains("Could not load type", StringComparison.Ordinal))
        {
            return KnownFault.StrippedTypeMissing;
        }
        if (message.Contains("DataAsset not found", StringComparison.Ordinal) ||
            message.Contains("Unknown rootId", StringComparison.Ordinal))
        {
            return KnownFault.WorldSetupIncomplete;
        }
        return KnownFault.Unknown;
    }
}

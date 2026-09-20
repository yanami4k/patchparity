using System.Text;
using System.Text.Json.Nodes;

namespace PatchParity.Mcp;

/// <summary>
/// 一次工具调用的结果。
///
/// 文本给人读；结构化内容给调用方消费，其中 evidence 键按约定承载可复算的证据，
/// 由调用方收进回执。证据有大小上限，超出则截断并标注，宁可截断也不让一次调用把回执撑爆。
/// </summary>
public sealed record ToolResult
{
    public required bool Ok { get; init; }
    public required string Text { get; init; }
    public JsonObject? Structured { get; init; }
    public string? Evidence { get; init; }

    /// <summary>证据上限，与回执侧的约定一致。</summary>
    public const int EvidenceLimitBytes = 16 * 1024;

    public static ToolResult Failure(string text) => new() { Ok = false, Text = text };

    public JsonObject ToContentObject()
    {
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = Text },
        };

        var structured = Structured is null ? new JsonObject() : (JsonObject)Structured.DeepClone();
        if (Evidence is not null)
        {
            structured["evidence"] = Truncate(Evidence);
        }

        return new JsonObject
        {
            ["content"] = content,
            ["structuredContent"] = structured,
            ["isError"] = !Ok,
        };
    }

    /// <summary>按字节截断，保留开头——证据的开头是判定与定位，尾部是明细。</summary>
    public static string Truncate(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= EvidenceLimitBytes)
        {
            return text;
        }

        const string suffix = "\n…证据超出上限已截断";
        int budget = EvidenceLimitBytes - Encoding.UTF8.GetByteCount(suffix);
        int taken = budget;

        // 不从多字节字符中间截断
        while (taken > 0 && (bytes[taken] & 0xC0) == 0x80)
        {
            taken--;
        }
        return Encoding.UTF8.GetString(bytes, 0, taken) + suffix;
    }
}

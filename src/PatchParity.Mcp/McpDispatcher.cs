using System.Text.Json;
using System.Text.Json.Nodes;

namespace PatchParity.Mcp;

/// <summary>
/// JSON-RPC 分发。只实现 stdio 传输下用得到的那几个方法，
/// 与输入输出解耦，故协议行为可脱离真实客户端测试。
/// </summary>
public sealed class McpDispatcher
{
    private readonly Tools _tools;

    public McpDispatcher(Tools tools) => _tools = tools;

    public const string ProtocolVersion = "2024-11-05";

    /// <summary>
    /// 处理一条请求，返回应答；通知（无 id）返回 null，按协议不得应答。
    /// </summary>
    public JsonObject? Handle(JsonNode? request)
    {
        if (request is not JsonObject obj)
        {
            return Error(null, -32600, "请求不是 JSON 对象");
        }

        string? method = obj["method"]?.GetValue<string>();
        JsonNode? id = obj["id"];

        if (method is null)
        {
            return Error(id, -32600, "缺少 method");
        }

        // 通知没有 id，处理后不应答
        if (id is null)
        {
            return null;
        }

        return method switch
        {
            "initialize" => Success(id, new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "patchparity", ["version"] = "0.1.0" },
            }),
            "tools/list" => Success(id, new JsonObject { ["tools"] = ToolList() }),
            "tools/call" => CallTool(id, obj["params"]),
            "ping" => Success(id, new JsonObject()),
            _ => Error(id, -32601, "不支持的方法: " + method),
        };
    }

    private JsonArray ToolList()
    {
        var array = new JsonArray();
        foreach (ToolSpec spec in _tools.Specs)
        {
            array.Add(new JsonObject
            {
                ["name"] = spec.Name,
                ["description"] = spec.Description,
                ["inputSchema"] = spec.InputSchema.DeepClone(),
                // 只读工具不产生副作用，调用方据此配置风险档
                ["annotations"] = new JsonObject { ["readOnlyHint"] = spec.ReadOnly },
            });
        }
        return array;
    }

    private JsonObject CallTool(JsonNode id, JsonNode? parameters)
    {
        string? name = parameters?["name"]?.GetValue<string>();
        if (name is null)
        {
            return Error(id, -32602, "缺少工具名");
        }

        JsonNode? argumentsNode = parameters?["arguments"];
        using JsonDocument document = JsonDocument.Parse(argumentsNode?.ToJsonString() ?? "{}");

        ToolResult result;
        try
        {
            result = _tools.Invoke(name, document.RootElement.Clone());
        }
        catch (Exception e)
        {
            // 工具内部异常按工具失败返回，不作为协议错误：调用方仍需拿到可读的失败说明
            result = ToolResult.Failure(e.GetType().Name + ": " + e.Message);
        }

        return Success(id, result.ToContentObject());
    }

    private static JsonObject Success(JsonNode id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}

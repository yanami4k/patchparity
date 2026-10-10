using System.Text;
using System.Text.Json.Nodes;

namespace PatchParity.Mcp;

/// <summary>
/// stdio 传输的入口：标准输入每行一条 JSON-RPC 请求，标准输出每行一条应答。
/// 标准输出只允许出现协议报文，任何诊断一律走标准错误，否则会污染报文流。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        using ServerSession? session = ServerSession.Create(args, ServerSession.NewSessionId());
        if (session is null)
        {
            Console.Error.WriteLine(ServerSession.Usage);
            return 2;
        }
        Console.Error.WriteLine("会话产物目录: " + session.SessionRoot);

        var dispatcher = new McpDispatcher(session.Tools);

        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        try
        {
            Serve(dispatcher, input, output);
        }
        finally
        {
            // 会话结束（输入流关闭或异常退出）时兜底撤销本会话施加过的改动，再放开工作区租约，
            // 避免把被测样例的源码留在改过的状态，影响下一个会话或下一个用例
            foreach (string failure in session.Close())
            {
                Console.Error.WriteLine("会话结束时撤销改动失败: " + failure);
            }
        }

        return 0;
    }

    private static void Serve(McpDispatcher dispatcher, StreamReader input, StreamWriter output)
    {
        while (input.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonObject? response;
            try
            {
                response = dispatcher.Handle(JsonNode.Parse(line));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("处理请求失败: " + e.Message);
                continue;
            }

            if (response is not null)
            {
                output.WriteLine(response.ToJsonString());
            }
        }
    }
}

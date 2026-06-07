using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TwinCatMcp.Server;

[McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echoes a message back. Used to verify the MCP server is wired up and reachable.")]
    public static string Echo([Description("The message to echo back")] string message)
        => $"TwinCAT MCP server is alive: {message}";
}

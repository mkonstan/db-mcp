using ModelContextProtocol;

namespace DbMcp.Server.Tools;

internal static class ToolHelper
{
    public static async Task<string> RunAsync(Func<Task<string>> body)
    {
        try
        {
            return await body();
        }
        catch (McpException)
        {
            // Already a protocol-layer exception with a curated message — rethrow as-is.
            throw;
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            // Q2 (2026-04-22): surface the real exception message instead of letting the MCP
            // SDK default-wrap this into a generic "An error occurred invoking '<tool>'" string.
            // For DbMcp's debug-focused workflow, the actual DB error (e.g., SQL error 207
            // "Invalid column name 'permaticker'", Postgres 42703 "column does not exist") IS
            // the diagnosis. Hiding it behind a generic wrapper forces Edan to read server logs
            // to see what actually failed. The inner exception is preserved for stack-trace
            // visibility server-side.
            throw new McpException(ex.Message, ex);
        }
    }
}

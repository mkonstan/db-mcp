using System.ComponentModel;
using DbMcp.Data.Models;
using DbMcp.Data.Services;
using ModelContextProtocol.Server;

namespace DbMcp.Server.Tools;

[McpServerToolType]
public sealed class ConnectionTools
{
    private readonly DatabaseService _db;

    public ConnectionTools(DatabaseService db) => _db = db;

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true),
     Description("List all configured database connections. Returns connection alias, engine type, and database name. Call this first to discover available connections.")]
    public Task<string> ListConnections()
    {
        return ToolHelper.RunAsync(() =>
        {
            var result = _db.ListConnections();
            return Task.FromResult(DynamicSerializer.Serialize(result));
        });
    }
}

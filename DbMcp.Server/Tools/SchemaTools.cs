using System.ComponentModel;
using DbMcp.Data.Models;
using DbMcp.Data.Services;
using ModelContextProtocol.Server;

namespace DbMcp.Server.Tools;

[McpServerToolType]
public sealed class SchemaTools
{
    private readonly DatabaseService _db;

    public SchemaTools(DatabaseService db) => _db = db;

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true),
     Description("List all schemas in the database.")]
    public Task<string> ListSchemas(
        [Description("Connection alias from config (e.g., 'tempdb-sql'). Use list_connections to see available connections.")] string connection,
        CancellationToken cancellationToken)
    {
        return ToolHelper.RunAsync(async () =>
        {
            var result = await _db.ListSchemasAsync(connection, cancellationToken);
            return DynamicSerializer.Serialize(result);
        });
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true),
     Description("List all tables and views in the database. Row counts are approximate on both engines (from catalog statistics, not a live COUNT); on SQL Server, views report 0.")]
    public Task<string> ListTables(
        [Description("Connection alias from config (e.g., 'tempdb-sql'). Use list_connections to see available connections.")] string connection,
        [Description("Database schema name (e.g., 'public', 'dbo'). Optional - if omitted, all schemas are included.")] string? schema = null,
        CancellationToken cancellationToken = default)
    {
        return ToolHelper.RunAsync(async () =>
        {
            var result = await _db.ListTablesAsync(connection, schema, cancellationToken);
            return DynamicSerializer.Serialize(result);
        });
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true),
     Description("Describe a table or view. Columns are returned for both tables and views; primary keys, indexes, and foreign keys are base-table-only — a view returns empty arrays for those.")]
    public Task<string> DescribeTable(
        [Description("Connection alias from config (e.g., 'tempdb-sql'). Use list_connections to see available connections.")] string connection,
        [Description("Table or view name (e.g., 'users', 'orders').")] string table,
        [Description("Database schema name (e.g., 'public', 'dbo'). Optional - if omitted, searches all schemas.")] string? schema = null,
        CancellationToken cancellationToken = default)
    {
        return ToolHelper.RunAsync(async () =>
        {
            var result = await _db.DescribeTableAsync(connection, table, schema, cancellationToken);
            return DynamicSerializer.Serialize(result);
        });
    }
}

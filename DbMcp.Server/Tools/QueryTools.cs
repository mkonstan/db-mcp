using System.ComponentModel;
using DbMcp.Data.Models;
using DbMcp.Data.Services;
using ModelContextProtocol.Server;

namespace DbMcp.Server.Tools;

[McpServerToolType]
public sealed class QueryTools
{
    private readonly DatabaseService _db;

    public QueryTools(DatabaseService db) => _db = db;

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true),
     Description("Run a read-only SELECT query. Returns a JSON object { \"rows\": [ ...row objects... ], \"returned_rows\": N } — the rows are under the \"rows\" key, NOT a bare top-level array. There is NO row cap — bound your query with TOP / LIMIT before calling. Only SELECT and WITH (CTE) statements are allowed. Errors after ~43 seconds if the query hasn't completed (wall-clock timeout, not a result-size limit).")]
    public Task<string> ExecuteQuery(
        [Description("Connection alias from config (e.g., 'tempdb-sql'). Use list_connections to see available connections.")] string connection,
        [Description("SQL SELECT query to execute. Must start with SELECT or WITH.")] string query,
        CancellationToken cancellationToken)
    {
        return ToolHelper.RunAsync(async () =>
        {
            var result = await _db.ExecuteQueryAsync(connection, query, cancellationToken);
            return DynamicSerializer.Serialize(result);
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false),
     Description("Run DDL/DML (CREATE, INSERT, UPDATE, DELETE, ALTER, DROP). With no batchSeparator, runs the statement as one command; returns {affected_rows, status}. With batchSeparator set, splits the SQL into batches on lines equal to the token and runs them in order; returns a per-batch array {status, batches:[{index, affected_rows}, ...]}. The useTransaction flag controls transaction wrapping independently of batchSeparator.")]
    public Task<string> ExecuteNonquery(
        [Description("Connection alias from config (e.g., 'tempdb-sql'). Use list_connections to see available connections.")] string connection,
        [Description("SQL DDL/DML statement to execute.")] string query,
        CancellationToken cancellationToken,
        [Description("Optional. A line-delimiter token (e.g. GO, ****). A line counts as a separator only if, after trimming whitespace, it equals this token EXACTLY (case-sensitive, nothing else on the line). Splits the SQL into batches; requires at least 2 non-empty batches or the call fails before executing anything. Pure text match — a token alone on a line inside a comment or string WILL split there, so pick a token that does not appear in your SQL. Leave empty for normal single-statement execution. Controls ONLY the result shape (flat vs per-batch array); it does not affect transactions.")] string? batchSeparator = null,
        [Description("Optional, default true. Applies whether or not batchSeparator is set. true = the statement (or all batches) run in ONE transaction; if anything fails, everything rolls back (atomic) and the response is {status:\"error\", failed_batch_index, error, rolled_back:true}. false = each batch commits as it runs (sqlcmd-style); on failure, earlier batches stay committed and the response lists each batch with a committed flag. Set false for statements that cannot run inside a transaction (e.g. CREATE DATABASE on SQL Server, CREATE INDEX CONCURRENTLY on Postgres) — including a single such statement with no batchSeparator.")] bool useTransaction = true)
    {
        return ToolHelper.RunAsync(async () =>
        {
            var result = await _db.ExecuteNonQueryAsync(connection, query, cancellationToken, batchSeparator, useTransaction);
            return DynamicSerializer.Serialize(result);
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false),
     Description("Read and execute a .sql file. With no batchSeparator, runs the whole file as one command; returns {file, affected_rows, status}. With batchSeparator set, splits the file into batches and runs them in order; returns {file, status, batches:[...]}. The useTransaction flag controls transaction wrapping independently of batchSeparator.")]
    public Task<string> ExecuteScript(
        [Description("Connection alias from config (e.g., 'tempdb-sql'). Use list_connections to see available connections.")] string connection,
        [Description("Absolute or relative path to a .sql file on the server's filesystem.")] string filePath,
        CancellationToken cancellationToken,
        [Description("Optional. A line-delimiter token (e.g. GO, ****). A line counts as a separator only if, after trimming whitespace, it equals this token EXACTLY (case-sensitive, nothing else on the line). Splits the SQL into batches; requires at least 2 non-empty batches or the call fails before executing anything. Pure text match — a token alone on a line inside a comment or string WILL split there, so pick a token that does not appear in your SQL. Leave empty for normal single-statement execution. Controls ONLY the result shape (flat vs per-batch array); it does not affect transactions.")] string? batchSeparator = null,
        [Description("Optional, default true. Applies whether or not batchSeparator is set. true = the statement (or all batches) run in ONE transaction; if anything fails, everything rolls back (atomic) and the response is {status:\"error\", failed_batch_index, error, rolled_back:true}. false = each batch commits as it runs (sqlcmd-style); on failure, earlier batches stay committed and the response lists each batch with a committed flag. Set false for statements that cannot run inside a transaction (e.g. CREATE DATABASE on SQL Server, CREATE INDEX CONCURRENTLY on Postgres). Default true preserves this tool's historical always-transactional behavior. Set false to run a lone non-transactional statement (e.g. CREATE DATABASE) from a .sql file — something this tool could not do before.")] bool useTransaction = true)
    {
        return ToolHelper.RunAsync(async () =>
        {
            var result = await _db.ExecuteScriptAsync(connection, filePath, cancellationToken, batchSeparator, useTransaction);
            return DynamicSerializer.Serialize(result);
        });
    }
}

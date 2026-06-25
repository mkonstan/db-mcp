using Dapper;
using DbMcp.Data.Configuration;
using DbMcp.Data.Providers;
using Microsoft.Extensions.Logging;
using Polly;

namespace DbMcp.Data.Services;

public sealed class DatabaseService
{
    private readonly Dictionary<string, ConnectionEntry> _connections;
    private readonly ILogger<DatabaseService> _logger;
    private readonly Dictionary<string, IDbEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResiliencePipeline> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _commandTimeoutSeconds;

    public DatabaseService(
        Dictionary<string, ConnectionEntry> connections,
        ILogger<DatabaseService> logger,
        ResilienceSettings resilienceSettings,
        DatabaseSettings databaseSettings)
    {
        _connections = connections;
        _logger = logger;
        _commandTimeoutSeconds = databaseSettings.CommandTimeoutSeconds;

        foreach (var (alias, entry) in connections)
        {
            var engine = CreateEngine(entry.Engine);
            _engines[alias] = engine;
            _pipelines[alias] = engine.BuildResiliencePipeline(resilienceSettings, databaseSettings, logger);
        }
    }

    private static IDbEngine CreateEngine(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Postgres => new PostgresEngine(),
        DatabaseEngine.SqlServer => new SqlServerEngine(),
        _ => throw new ArgumentException($"Unsupported database engine: {engine}")
    };

    private static void RequireNotEmpty(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"'{paramName}' is required and cannot be empty", paramName);
    }

    private (IDbEngine engine, string connectionString) ResolveConnection(string alias)
    {
        RequireNotEmpty(alias, "connection");

        if (!_connections.TryGetValue(alias, out var entry))
        {
            var available = string.Join(", ", _connections.Keys);
            throw new ArgumentException(
                $"Connection '{alias}' not found. Available connections: {available}", "connection");
        }

        return (_engines[alias], entry.ConnectionString);
    }

    // ---- Public methods (one per tool) ----

    public List<Dictionary<string, object?>> ListConnections()
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var (alias, entry) in _connections)
        {
            result.Add(new Dictionary<string, object?>
            {
                ["name"] = alias,
                ["engine"] = entry.Engine.ToString(),
                ["database"] = entry.GetDatabaseName()
            });
        }
        return result;
    }

    public async Task<List<Dictionary<string, object?>>> ListSchemasAsync(
        string connection, CancellationToken ct)
    {
        var (engine, connStr) = ResolveConnection(connection);
        var sql = engine.ListSchemasSql;
        var rows = await ExecuteAsync(connection, sql, null, ct);
        return ResultMapper.ToSerializable(rows);
    }

    public async Task<List<Dictionary<string, object?>>> ListTablesAsync(
        string connection, string? schema, CancellationToken ct)
    {
        var (engine, _) = ResolveConnection(connection);
        var sql = engine.ListTablesSql(schema);
        var parameters = string.IsNullOrEmpty(schema) ? null : new { schema };
        var rows = await ExecuteAsync(connection, sql, parameters, ct);
        return ResultMapper.ToSerializable(rows);
    }

    public async Task<Dictionary<string, object?>> DescribeTableAsync(
        string connection, string table, string? schema, CancellationToken ct)
    {
        RequireNotEmpty(table, "table");
        var (engine, _) = ResolveConnection(connection);

        var parameters = string.IsNullOrEmpty(schema)
            ? (object)new { table }
            : new { table, schema };

        var columnsTask = ExecuteAsync(connection, engine.DescribeTableSql(table, schema), parameters, ct);
        var pksTask = ExecuteAsync(connection, engine.PrimaryKeysSql(table, schema), parameters, ct);
        var indexesTask = ExecuteAsync(connection, engine.IndexesSql(table, schema), parameters, ct);
        var fksTask = ExecuteAsync(connection, engine.ForeignKeysSql(table, schema), parameters, ct);

        await Task.WhenAll(columnsTask, pksTask, indexesTask, fksTask);

        return new Dictionary<string, object?>
        {
            ["columns"] = ResultMapper.ToSerializable(await columnsTask),
            ["primary_keys"] = ResultMapper.ToSerializable(await pksTask),
            ["indexes"] = ResultMapper.ToSerializable(await indexesTask),
            ["foreign_keys"] = ResultMapper.ToSerializable(await fksTask)
        };
    }

    /// <summary>
    /// Runs a read-only SELECT/WITH query and returns the uniform multi-set read envelope
    /// <c>{query, results:[{fields, rows, row_count}]}</c> — one entry in <c>results</c> per result set
    /// the command produced.
    /// </summary>
    /// <remarks>
    /// JOB — materialize EVERY result set (a single command can return more than one, e.g. multi-SELECT
    /// or a proc) into a dictionary graph the model reads with one fixed access path.
    /// <para>
    /// WHY always enveloped, even for the common single-set case (the rejected fork: stay flat when
    /// there is exactly one set): a query expected to return one set can return two, so a
    /// sometimes-flat-sometimes-nested shape is a footgun. <c>results</c> is ALWAYS an array; the model
    /// reads <c>results[0].rows</c> and never branches on set-count. A 0-row SELECT is a result set with
    /// populated <c>fields</c> and an empty <c>rows</c> — NOT zero result sets.
    /// </para>
    /// <para>
    /// WHY fields are read from reader metadata BEFORE the rows are materialized: the column schema does
    /// not depend on any row existing, so an empty set still reports its columns. <c>type</c> is the
    /// DB-native type name (<see cref="System.Data.Common.DbDataReader.GetDataTypeName(int)"/>) — engine-specific,
    /// depends on the connected database (SQL Server <c>int</c>/<c>varchar</c>, PostgreSQL <c>int4</c>/<c>varchar</c>) — the vocabulary an LLM writing follow-up SQL reasons in; the CLR type
    /// is .NET-internal noise here.
    /// </para>
    /// <para>
    /// WHY each set's rows are fully materialized BEFORE <c>NextResultAsync</c> (the rejected fork:
    /// advance the reader first, enumerate the lazy <c>Parse()</c> result later): <c>reader.Parse()</c>
    /// is a LAZY enumerator over the CURRENT set; advancing first invalidates it and silently drops rows
    /// with no exception. The <c>.ToList()</c> inside <see cref="ResultMapper.ToSerializable"/> forces
    /// materialization at the right moment. The per-cell DBNull→null normalization the old reader loop
    /// did is gone — DapperRow already does it (see <see cref="ResultMapper"/>). Row iteration inside
    /// <c>Parse()</c> is synchronous over the buffered reader; only set advancement stays async — an
    /// accepted shift from the prior ReadAsync loop.
    /// </para>
    /// The no-row-cap / no-truncation / 43s-wall-clock philosophy is unchanged — see the comment block
    /// inside the callback.
    /// </remarks>
    public async Task<Dictionary<string, object?>> ExecuteQueryAsync(
        string connection, string query, CancellationToken ct)
    {
        RequireNotEmpty(query, "query");

        var trimmed = query.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "execute_query only accepts SELECT or WITH (CTE) statements. Use execute_nonquery for DDL/DML.", "query");
        }

        var (engine, connStr) = ResolveConnection(connection);
        var pipeline = _pipelines[connection];

        _logger.LogInformation(
            "execute_query on '{Connection}' (timeout={TimeoutSeconds}s): {Query}",
            connection, _commandTimeoutSeconds, query);

        var results = await pipeline.ExecuteAsync(async token =>
        {
            await using var conn = engine.CreateConnection(connStr);
            await conn.OpenAsync(token);
            // DbMcp is a magic-free database tool by design. The reader returns whatever
            // the query produced — there is NO row cap, NO truncation, NO `truncated` flag.
            // The ONLY safety net is Database.CommandTimeoutSeconds (default 43s), which is a
            // wall-clock guardrail against hangs, NOT a result-size guardrail against caller
            // carelessness. Result-size discipline lives in the caller (Edan), not the server.
            //
            // This is intentional. Earlier versions of this code had:
            //   (a) WrapWithLimit — a pre-flight string-surgery transform that injected TOP/LIMIT
            //       into the user's query. It mangled queries containing TOP, OFFSET FETCH, UNION,
            //       CTEs, and comments. Deleted 2026-04-08.
            //   (b) An in-loop reader-break that capped at MaxQueryRows + 1. It worked for queries
            //       that streamed within the timeout, but was structurally unreachable on unbounded
            //       scans (ExecuteReaderAsync blocks waiting for the first batch buffer). Deleted
            //       2026-04-08.
            //   (c) A post-loop Take(MaxQueryRows) trim. Cosmetic; deleted with (b).
            //
            // SqlKata-based wrapping was also evaluated and rejected (NOT VIABLE) because
            // FromRaw/WithRaw create derived tables that inherit the same semantic bugs as
            // WrapWithLimit (ORDER BY discardable in subqueries, CTEs illegal nested, dialect
            // passthrough). See _scratch/databasemcp_sqlkata_wrap_research.md.
            //
            // The discipline that makes this safe lives at the caller layer: workspace game rule
            // G16 ("Edan bounds queries before sending — never relies on server scaffolding for
            // safety"). Edan must (1) know the approximate row count of the table being queried
            // before fetching, and (2) default to TOP 10 / LIMIT 10 unless a different bound is
            // explicitly justified. With G16 in place, the server-side row cap was redundant
            // safety. Without G16, stripping the cap would be removing safety — but G16 is in
            // place, so this code is honest, not unsafe.
            //
            // Do NOT re-add server-side row capping in any form (string surgery, query wrapping,
            // reader-loop early-break, post-fetch truncation). Every previous attempt has either
            // silently corrupted user queries or failed to bound the actual problem. The right
            // place to fix accidental unbounded queries is at the caller, not the server.
            //
            // See _scratch/databasemcp_strip_rowcap_blueprint.md for the rewrite that landed this.
            using var reader = await conn.ExecuteReaderAsync(
                new CommandDefinition(query, commandTimeout: _commandTimeoutSeconds, cancellationToken: token));

            var sets = new List<Dictionary<string, object?>>();
            do
            {
                // Fields FIRST, off reader metadata — independent of any row existing, so an
                // empty set still reports its columns. type = DB-native (GetDataTypeName).
                var fields = new List<Dictionary<string, object?>>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    fields.Add(new Dictionary<string, object?>
                    {
                        ["name"] = reader.GetName(i),
                        ["type"] = reader.GetDataTypeName(i)
                    });
                }

                // Rows next: Parse() (non-generic → DapperRow) is LAZY over THIS set only.
                // ToSerializable's .ToList() MUST complete before NextResultAsync below, or the
                // enumerator is invalidated and rows are silently lost. DapperRow already nulls DBNull.
                var rows = ResultMapper.ToSerializable(reader.Parse());

                sets.Add(new Dictionary<string, object?>
                {
                    ["fields"] = fields,
                    ["rows"] = rows,
                    ["row_count"] = rows.Count
                });
            } while (await reader.NextResultAsync(token));

            _logger.LogDebug("Query returned {Count} result set(s)", sets.Count);
            return sets;
        }, ct);

        return new Dictionary<string, object?>
        {
            ["query"] = query,
            ["results"] = results
        };
    }

    public async Task<Dictionary<string, object?>> ExecuteNonQueryAsync(
        string connection, string query, CancellationToken ct,
        string? batchSeparator = null, bool useTransaction = true)
    {
        RequireNotEmpty(query, "query");

        var batches = ResolveBatches(query, batchSeparator, out var wasSplit);

        _logger.LogInformation(
            "execute_nonquery on '{Connection}' (timeout={TimeoutSeconds}s, batches={BatchCount}, useTransaction={UseTransaction}): {Query}",
            connection, _commandTimeoutSeconds, batches.Count, useTransaction, query);

        var run = await ExecuteBatchesAsync(connection, batches, useTransaction, ct);
        return ProjectResult(run, wasSplit, file: null);
    }

    public async Task<Dictionary<string, object?>> ExecuteScriptAsync(
        string connection, string filePath, CancellationToken ct,
        string? batchSeparator = null, bool useTransaction = true)
    {
        RequireNotEmpty(filePath, "filePath");

        if (!Path.GetExtension(filePath).Equals(".sql", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("File must have a .sql extension", "filePath");

        if (!File.Exists(filePath))
            throw new ArgumentException($"File not found: {filePath}", "filePath");

        var scriptContent = await File.ReadAllTextAsync(filePath, ct);

        if (string.IsNullOrWhiteSpace(scriptContent))
            throw new ArgumentException("Script file is empty", "filePath");

        var batches = ResolveBatches(scriptContent, batchSeparator, out var wasSplit);

        _logger.LogInformation(
            "execute_script on '{Connection}' from '{FilePath}' ({ByteCount} bytes, timeout={TimeoutSeconds}s, batches={BatchCount}, useTransaction={UseTransaction}): {Script}",
            connection, filePath, scriptContent.Length, _commandTimeoutSeconds, batches.Count, useTransaction, scriptContent);

        var run = await ExecuteBatchesAsync(connection, batches, useTransaction, ct);
        return ProjectResult(run, wasSplit, file: filePath);
    }

    // ---- Batch execution (shared core for nonquery + script) ----

    /// <summary>
    /// Builds the batch list from <paramref name="sql"/> and tells the caller whether a split happened.
    /// </summary>
    /// <remarks>
    /// JOB — turn the raw SQL into the ordered batches the core will run, on ONE code path with no
    /// behavior fork. No separator → a single-element list (the whole input is one batch). Separator
    /// → <see cref="BatchSplitter.Split"/> plus the "at least 2 non-empty batches" policy.
    /// <para>
    /// WHY the ≥2 guard lives here, not in the splitter — the error message names the tool parameter,
    /// and it fires BEFORE any connection opens (an <see cref="ArgumentException"/> that
    /// ToolHelper.RunAsync converts to an McpException). <paramref name="wasSplit"/> is the ONLY
    /// signal that drives the result shape at the return boundary; it is independent of
    /// <c>useTransaction</c>, which keeps the two axes orthogonal.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> ResolveBatches(string sql, string? batchSeparator, out bool wasSplit)
    {
        wasSplit = !string.IsNullOrWhiteSpace(batchSeparator);
        if (!wasSplit)
            return new[] { sql };

        var batches = BatchSplitter.Split(sql, batchSeparator!);
        if (batches.Count < 2)
        {
            throw new ArgumentException(
                $"batchSeparator '{batchSeparator}' produced {batches.Count} non-empty batch(es); " +
                "need at least 2 to run a split execution. Remove batchSeparator to run as a single statement.",
                nameof(batchSeparator));
        }

        return batches;
    }

    /// <summary>
    /// Runs the batches (N=1 with no separator, N≥2 when split) and returns a shape-agnostic
    /// <see cref="BatchRunResult"/>. The transaction wrapper is decided ONLY by <paramref name="useTransaction"/>.
    /// </summary>
    /// <remarks>
    /// The entire connection + loop lives inside ONE <c>pipeline.ExecuteAsync</c> callback: the
    /// connection must stay open across all batches and cannot outlive the callback, so the callback
    /// owns open → loop → commit/rollback → return (cf. CALLBACK-SCOPED RESOURCE LIFECYCLE). This holds
    /// for N=1 too, so the no-separator path is exactly today's single-statement wrap.
    /// </remarks>
    private async Task<BatchRunResult> ExecuteBatchesAsync(
        string connection, IReadOnlyList<string> batches, bool useTransaction, CancellationToken ct)
    {
        var (engine, connStr) = ResolveConnection(connection);
        var pipeline = _pipelines[connection];

        if (useTransaction)
        {
            try
            {
                return await pipeline.ExecuteAsync(async token =>
                {
                    await using var conn = engine.CreateConnection(connStr);
                    await conn.OpenAsync(token);
                    await using var transaction = await conn.BeginTransactionAsync(token);

                    var entries = new List<Dictionary<string, object?>>(batches.Count);
                    var index = 0;
                    try
                    {
                        for (; index < batches.Count; index++)
                        {
                            var affected = await conn.ExecuteAsync(new CommandDefinition(
                                batches[index], transaction: transaction,
                                commandTimeout: _commandTimeoutSeconds, cancellationToken: token));
                            entries.Add(new Dictionary<string, object?>
                            {
                                ["index"] = index,
                                ["affected_rows"] = affected
                            });
                        }

                        await transaction.CommitAsync(token);
                        return BatchRunResult.Succeeded(entries);
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync(token);
                        throw new BatchExecutionException(index, ex);
                    }
                }, ct);
            }
            catch (BatchExecutionException ex)
            {
                // A failed batch under useTransaction=true is a reportable result, not a server fault:
                // everything rolled back. Project to the atomic-failure shape (no per-batch committed
                // flags — they would lie after a rollback). Caught OUTSIDE pipeline.ExecuteAsync so it
                // never propagates as an unhandled throw.
                return BatchRunResult.Failed(
                    Array.Empty<Dictionary<string, object?>>(), ex.FailedIndex, ex.Message, rolledBack: true);
            }
        }

        // useTransaction == false: sqlcmd-style, each batch auto-commits as it runs, stop on first error.
        //
        // Polly retry re-runs the whole callback; under non-transactional batching that would re-apply
        // already-committed batches (double application). Retry is off by default (D11) and the
        // transient predicate excludes the statement-level errors a batch script fails on (syntax /
        // constraint errors are not transient), so this is safe today. If retry is ever enabled
        // broadly, non-transactional batch execution must be excluded from the retry scope.
        return await pipeline.ExecuteAsync(async token =>
        {
            await using var conn = engine.CreateConnection(connStr);
            await conn.OpenAsync(token);

            var entries = new List<Dictionary<string, object?>>(batches.Count);
            for (var index = 0; index < batches.Count; index++)
            {
                try
                {
                    var affected = await conn.ExecuteAsync(new CommandDefinition(
                        batches[index], commandTimeout: _commandTimeoutSeconds, cancellationToken: token));
                    entries.Add(new Dictionary<string, object?>
                    {
                        ["index"] = index,
                        ["affected_rows"] = affected,
                        ["committed"] = true
                    });
                }
                catch (Exception ex)
                {
                    entries.Add(new Dictionary<string, object?>
                    {
                        ["index"] = index,
                        ["error"] = ex.Message,
                        ["committed"] = false
                    });
                    return BatchRunResult.Failed(entries, index, ex.Message, rolledBack: false);
                }
            }

            return BatchRunResult.Succeeded(entries);
        }, ct);
    }

    /// <summary>
    /// Maps a <see cref="BatchRunResult"/> to the serialized shape. SHAPE is keyed ONLY on
    /// <paramref name="wasSplit"/> (separator-presence) — never on the transaction mode.
    /// </summary>
    /// <remarks>
    /// No separator → today's flat shape (the natural projection of the one-element batch result).
    /// Separator → the per-batch array. This is a pure mapping over the core's return: it never
    /// re-runs SQL and never inspects <c>useTransaction</c>, which is what keeps the shape and
    /// transaction axes orthogonal. An atomic (rolled-back) failure carries no per-batch entries, so
    /// it projects to <c>{status:error, failed_batch_index, error, rolled_back:true}</c> in both the
    /// flat and split cases.
    /// </remarks>
    internal static Dictionary<string, object?> ProjectResult(BatchRunResult run, bool wasSplit, string? file)
    {
        if (run.Success)
        {
            if (wasSplit)
            {
                var result = new Dictionary<string, object?> { ["status"] = "success" };
                if (file is not null) result["file"] = file;
                result["batches"] = run.Entries;
                return result;
            }

            var single = run.Entries[0];
            var flat = new Dictionary<string, object?>();
            if (file is not null) flat["file"] = file;
            flat["affected_rows"] = single["affected_rows"];
            flat["status"] = "success";
            return flat;
        }

        // Failure. Under useTransaction=true (RolledBack) the only meaningful facts are which batch
        // failed and that everything was undone — the atomic-failure shape applies in BOTH the flat
        // and split cases (it carries no per-batch entries, so wasSplit cannot change it).
        if (run.RolledBack)
        {
            var atomic = new Dictionary<string, object?> { ["status"] = "error" };
            if (file is not null) atomic["file"] = file;
            atomic["failed_batch_index"] = run.FailedIndex;
            atomic["error"] = run.Error;
            atomic["rolled_back"] = true;
            return atomic;
        }

        // useTransaction=false failure. Shape still keyed on wasSplit (orthogonality): split → the
        // per-batch array with honest committed flags; no-separator → the flat one-element error.
        if (wasSplit)
        {
            var partial = new Dictionary<string, object?> { ["status"] = "error" };
            if (file is not null) partial["file"] = file;
            partial["batches"] = run.Entries;
            return partial;
        }

        var flatError = new Dictionary<string, object?> { ["status"] = "error" };
        if (file is not null) flatError["file"] = file;
        flatError["error"] = run.Error;
        flatError["committed"] = false;
        return flatError;
    }

    // ---- Internal helpers ----

    private async Task<IEnumerable<dynamic>> ExecuteAsync(
        string alias, string sql, object? parameters, CancellationToken ct)
    {
        var (engine, connStr) = ResolveConnection(alias);
        var pipeline = _pipelines[alias];

        _logger.LogInformation(
            "introspection query on '{Connection}' (timeout={TimeoutSeconds}s): {Query}",
            alias, _commandTimeoutSeconds, sql);

        return await pipeline.ExecuteAsync(async token =>
        {
            await using var conn = engine.CreateConnection(connStr);
            await conn.OpenAsync(token);
            var cmd = new CommandDefinition(sql, parameters, commandTimeout: _commandTimeoutSeconds, cancellationToken: token);
            var result = (await conn.QueryAsync(cmd)).ToList();
            _logger.LogDebug("Query returned {Count} rows", result.Count);
            return (IEnumerable<dynamic>)result;
        }, ct);
    }
}

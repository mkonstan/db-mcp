using System.Data.Common;
using DbMcp.Data.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Polly;
using Polly.Retry;

namespace DbMcp.Data.Providers;

public sealed class PostgresEngine : IDbEngine
{
    public DbConnection CreateConnection(string connectionString)
        => new NpgsqlConnection(connectionString);

    public string ListSchemasSql =>
        """
        SELECT schema_name
        FROM information_schema.schemata
        WHERE schema_name NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
        ORDER BY schema_name
        """;

    public string ListTablesSql(string? schema)
    {
        var filter = string.IsNullOrEmpty(schema)
            ? "AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')"
            : "AND n.nspname = @schema";

        return $"""
            SELECT n.nspname AS schema_name,
                   c.relname AS table_name,
                   CASE c.relkind WHEN 'r' THEN 'TABLE' WHEN 'v' THEN 'VIEW' END AS type,
                   c.reltuples::bigint AS approximate_row_count
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'v')
              {filter}
            ORDER BY n.nspname, c.relname
            """;
    }

    public string DescribeTableSql(string table, string? schema)
    {
        var schemaFilter = string.IsNullOrEmpty(schema)
            ? ""
            : "AND table_schema = @schema";

        return $"""
            SELECT column_name, data_type, is_nullable, column_default, ordinal_position
            FROM information_schema.columns
            WHERE table_name = @table
              {schemaFilter}
            ORDER BY ordinal_position
            """;
    }

    public string PrimaryKeysSql(string table, string? schema)
    {
        var schemaFilter = string.IsNullOrEmpty(schema)
            ? ""
            : "AND n.nspname = @schema";

        return $"""
            SELECT a.attname AS column_name
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY(i.indkey)
            WHERE c.relname = @table
              AND i.indisprimary = true
              {schemaFilter}
            ORDER BY a.attnum
            """;
    }

    public string IndexesSql(string table, string? schema)
    {
        var schemaFilter = string.IsNullOrEmpty(schema)
            ? ""
            : "AND schemaname = @schema";

        return $"""
            SELECT indexname AS index_name,
                   indexdef AS index_definition
            FROM pg_indexes
            WHERE tablename = @table
              {schemaFilter}
            ORDER BY indexname
            """;
    }

    public string ForeignKeysSql(string table, string? schema)
    {
        var schemaFilter = string.IsNullOrEmpty(schema)
            ? ""
            : "AND tc.table_schema = @schema";

        return $"""
            SELECT tc.constraint_name,
                   kcu.column_name,
                   ccu.table_schema AS referenced_schema,
                   ccu.table_name AS referenced_table,
                   ccu.column_name AS referenced_column
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
              AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
              ON ccu.constraint_name = tc.constraint_name
              AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_name = @table
              {schemaFilter}
            ORDER BY tc.constraint_name, kcu.ordinal_position
            """;
    }

    private static readonly HashSet<string> TransientPgSqlStates = new()
    {
        "40001", // serialization_failure
        "40P01", // deadlock_detected
        "53300", // too_many_connections
        "57P03", // cannot_connect_now
        "08000", "08001", "08003", "08004", "08006" // connection exception family
        // NOTE: 57014 (query_canceled, the Pg equivalent of SQL -2) intentionally
        // EXCLUDED for the same reason — retrying a timed-out query burns another
        // full timeout window for no gain.
    };

    public ResiliencePipeline BuildResiliencePipeline(
        ResilienceSettings resilience,
        DatabaseSettings database,
        ILogger logger)
    {
        var builder = new ResiliencePipelineBuilder();

        if (resilience.Retry.Enabled)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = resilience.Retry.MaxAttempts,
                Delay = TimeSpan.FromMilliseconds(resilience.Retry.BaseDelayMs),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder()
                    .Handle<NpgsqlException>(ex =>
                        ex.IsTransient ||
                        (ex is PostgresException pg && TransientPgSqlStates.Contains(pg.SqlState))),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Retry attempt {Attempt} after {Delay}ms due to transient error: {Message}",
                        args.AttemptNumber,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            });
        }

        if (resilience.Timeout.Enabled)
        {
            // Staggered pair: Polly fires 5s AFTER CommandTimeout so the DB
            // engine cancels the query cleanly first. See appsettings.json
            // comment on Database.CommandTimeoutSeconds.
            builder.AddTimeout(TimeSpan.FromSeconds(database.CommandTimeoutSeconds + 5));
        }

        return builder.Build();
    }
}

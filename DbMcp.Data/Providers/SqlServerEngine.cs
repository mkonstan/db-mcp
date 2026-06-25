using System.Data.Common;
using DbMcp.Data.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace DbMcp.Data.Providers;

public sealed class SqlServerEngine : IDbEngine
{
    public DbConnection CreateConnection(string connectionString)
        => new SqlConnection(connectionString);

    public string ListSchemasSql =>
        """
        SELECT name AS schema_name
        FROM sys.schemas
        WHERE schema_id < 16384
          AND name NOT IN ('guest', 'INFORMATION_SCHEMA', 'sys')
        ORDER BY name
        """;

    public string ListTablesSql(string? schema)
    {
        var schemaFilter = string.IsNullOrEmpty(schema)
            ? ""
            : "AND s.name = @schema";

        return $"""
            SELECT s.name AS schema_name,
                   t.name AS table_name,
                   'TABLE' AS type,
                   SUM(p.rows) AS approximate_row_count
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
            WHERE 1=1
              {schemaFilter}
            GROUP BY s.name, t.name

            UNION ALL

            SELECT s.name AS schema_name,
                   v.name AS table_name,
                   'VIEW' AS type,
                   0 AS approximate_row_count
            FROM sys.views v
            JOIN sys.schemas s ON s.schema_id = v.schema_id
            WHERE 1=1
              {schemaFilter}

            ORDER BY schema_name, table_name
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
            : "AND s.name = @schema";

        return $"""
            SELECT c.name AS column_name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE i.is_primary_key = 1
              AND t.name = @table
              {schemaFilter}
            ORDER BY ic.key_ordinal
            """;
    }

    public string IndexesSql(string table, string? schema)
    {
        var schemaFilter = string.IsNullOrEmpty(schema)
            ? ""
            : "AND s.name = @schema";

        return $"""
            SELECT i.name AS index_name,
                   i.type_desc AS index_type,
                   i.is_unique,
                   STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS columns
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.name = @table
              AND i.name IS NOT NULL
              {schemaFilter}
            GROUP BY i.name, i.type_desc, i.is_unique
            ORDER BY i.name
            """;
    }

    public string ForeignKeysSql(string table, string? schema)
    {
        var schemaFilter = string.IsNullOrEmpty(schema)
            ? ""
            : "AND ps.name = @schema";

        return $"""
            SELECT fk.name AS constraint_name,
                   pc.name AS column_name,
                   rs.name AS referenced_schema,
                   rt.name AS referenced_table,
                   rc.name AS referenced_column
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            WHERE pt.name = @table
              {schemaFilter}
            ORDER BY fk.name
            """;
    }

    private static readonly HashSet<int> TransientSqlErrorNumbers = new()
    {
        // Connection-layer network errors
        20, 64, 121, 233,
        10053, 10054, 10060, 10061, 11001,
        // Lock/deadlock
        1204, 1205,
        // Azure SQL throttle/failover (harmless to include for local)
        40197, 40501, 40613, 49918, 49919, 49920
        // NOTE: -2 (command timeout) intentionally EXCLUDED — retrying a
        // timed-out query burns another full timeout window for no gain.
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
                    .Handle<SqlException>(ex => ex.Errors
                        .Cast<SqlError>()
                        .Any(e => TransientSqlErrorNumbers.Contains(e.Number))),
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

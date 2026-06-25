using System.Data.Common;
using DbMcp.Data.Configuration;
using Microsoft.Extensions.Logging;
using Polly;

namespace DbMcp.Data.Providers;

public interface IDbEngine
{
    DbConnection CreateConnection(string connectionString);
    string ListSchemasSql { get; }
    string ListTablesSql(string? schema);
    string DescribeTableSql(string table, string? schema);
    string PrimaryKeysSql(string table, string? schema);
    string IndexesSql(string table, string? schema);
    string ForeignKeysSql(string table, string? schema);
    ResiliencePipeline BuildResiliencePipeline(ResilienceSettings resilience, DatabaseSettings database, ILogger logger);
}

using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbMcp.Data.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseEngine
{
    Postgres,
    SqlServer
}

public sealed record ConnectionEntry
{
    public required DatabaseEngine Engine { get; init; }
    public required string ConnectionString { get; init; }

    public string GetDatabaseName()
    {
        return Engine switch
        {
            DatabaseEngine.Postgres => new NpgsqlConnectionStringBuilder(ConnectionString).Database ?? "",
            DatabaseEngine.SqlServer => new SqlConnectionStringBuilder(ConnectionString).InitialCatalog,
            _ => ""
        };
    }
}

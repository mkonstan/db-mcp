using DbMcp.Data.Configuration;
using DbMcp.Data.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbMcp.Data.Tests;

public class DatabaseServiceTests
{
    private static DatabaseService CreateService(Dictionary<string, ConnectionEntry>? connections = null)
    {
        connections ??= new Dictionary<string, ConnectionEntry>
        {
            ["test-pg"] = new()
            {
                Engine = DatabaseEngine.Postgres,
                ConnectionString = "Host=localhost;Database=testdb;Username=postgres;Password=postgres"
            },
            ["test-sql"] = new()
            {
                Engine = DatabaseEngine.SqlServer,
                ConnectionString = "Server=localhost;Database=testdb;Trusted_Connection=True;"
            }
        };

        return new DatabaseService(
            connections,
            NullLogger<DatabaseService>.Instance,
            new ResilienceSettings(),
            new DatabaseSettings());
    }

    [Fact]
    public void ListConnections_ReturnsAllConfiguredConnections()
    {
        var svc = CreateService();
        var result = svc.ListConnections();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => (string)c["name"]! == "test-pg");
        Assert.Contains(result, c => (string)c["name"]! == "test-sql");
    }

    [Fact]
    public void ListConnections_IncludesDatabaseName()
    {
        var svc = CreateService();
        var result = svc.ListConnections();

        var pg = result.First(c => (string)c["name"]! == "test-pg");
        Assert.Equal("testdb", pg["database"]);
    }

    [Fact]
    public void ListConnections_IncludesEngineType()
    {
        var svc = CreateService();
        var result = svc.ListConnections();

        var sql = result.First(c => (string)c["name"]! == "test-sql");
        Assert.Equal("SqlServer", sql["engine"]);
    }

    [Fact]
    public async Task ResolveConnection_ThrowsForUnknownAlias()
    {
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ListSchemasAsync("nonexistent", CancellationToken.None));

        Assert.Contains("nonexistent", ex.Message);
        Assert.Contains("Available connections", ex.Message);
    }

    [Fact]
    public async Task ExecuteQuery_RejectsNonSelectStatements()
    {
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ExecuteQueryAsync("test-pg", "DELETE FROM users", CancellationToken.None));

        Assert.Contains("SELECT", ex.Message);
    }

    [Fact]
    public async Task ExecuteQuery_RejectsEmptyQuery()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ExecuteQueryAsync("test-pg", "", CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteScript_RejectsNonSqlExtension()
    {
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ExecuteScriptAsync("test-pg", "script.txt", CancellationToken.None));

        Assert.Contains(".sql", ex.Message);
    }

    [Fact]
    public async Task ExecuteScript_RejectsMissingFile()
    {
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ExecuteScriptAsync("test-pg", "nonexistent.sql", CancellationToken.None));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteNonQuery_BatchSeparatorWithOneBatch_Throws()
    {
        var svc = CreateService();

        // The ≥2-batch guard fires before any connection opens (mirrors the ExecuteQuery_Rejects*
        // tests that throw pre-connection). "SELECT 1" with a GO separator yields one batch.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ExecuteNonQueryAsync("test-pg", "SELECT 1", CancellationToken.None, batchSeparator: "GO"));

        Assert.Contains("at least 2", ex.Message);
    }

    [Fact]
    public async Task ExecuteScript_BatchSeparatorWithOneBatch_Throws()
    {
        var svc = CreateService();
        var path = Path.Combine(Path.GetTempPath(), $"batchsplit_{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, "SELECT 1");

        try
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => svc.ExecuteScriptAsync("test-pg", path, CancellationToken.None, batchSeparator: "GO"));

            Assert.Contains("at least 2", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteNonQuery_EmptySeparator_DoesNotThrowBatchGuard()
    {
        var svc = CreateService();

        // Orthogonality pin: an empty separator builds a 1-element batch list and proceeds to open a
        // connection (which fails — there is no live testdb). The ≥2 guard is gated on
        // separator-presence ONLY, so whatever exception surfaces must NOT be the "at least 2" guard.
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => svc.ExecuteNonQueryAsync("test-pg", "SELECT 1", CancellationToken.None, batchSeparator: null));

        Assert.DoesNotContain("at least 2", ex.Message);
    }
}

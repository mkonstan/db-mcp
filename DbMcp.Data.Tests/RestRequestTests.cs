using System.Text.Json;

namespace DbMcp.Data.Tests;

/// <summary>
/// Offline contract tests for the REST request DTOs. They pin the wire shape Scalar/curl drive and
/// prove the optional batch fields default to behavior-preserving values for existing callers.
/// </summary>
/// <remarks>
/// The DTOs under test mirror QueryRequest / ScriptRequest defined in DbMcp.Server/Program.cs.
/// They are mirrored here rather than referenced because the Server project is a self-contained web
/// app (Microsoft.NET.Sdk.Web + win-x64 RID); referencing it would drag the entire ASP.NET host into
/// this offline-only xunit suite. These tests lock the deserialization SEMANTICS (default-value
/// binding + camelCase web binding) the REST stack relies on; if the real records drift from this
/// shape, the parity change has regressed and the smoke run will catch the wiring.
/// </remarks>
public class RestRequestTests
{
    // JsonSerializerDefaults.Web matches ASP.NET Core minimal-API model binding (camelCase,
    // case-insensitive property names).
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private record QueryRequest(string Query, string? BatchSeparator = null, bool UseTransaction = true);
    private record ScriptRequest(string FilePath, string? BatchSeparator = null, bool UseTransaction = true);

    [Fact]
    public void QueryRequest_DefaultsPreserveBehavior()
    {
        var body = JsonSerializer.Deserialize<QueryRequest>("""{"query":"SELECT 1"}""", WebOptions)!;

        Assert.Equal("SELECT 1", body.Query);
        Assert.Null(body.BatchSeparator);
        Assert.True(body.UseTransaction);
    }

    [Fact]
    public void ScriptRequest_DefaultsPreserveBehavior()
    {
        var body = JsonSerializer.Deserialize<ScriptRequest>("""{"filePath":"x.sql"}""", WebOptions)!;

        Assert.Equal("x.sql", body.FilePath);
        Assert.Null(body.BatchSeparator);
        Assert.True(body.UseTransaction);
    }

    [Fact]
    public void QueryRequest_BindsBatchParams()
    {
        var body = JsonSerializer.Deserialize<QueryRequest>(
            """{"query":"A GO B","batchSeparator":"GO","useTransaction":false}""", WebOptions)!;

        Assert.Equal("GO", body.BatchSeparator);
        Assert.False(body.UseTransaction);
    }
}

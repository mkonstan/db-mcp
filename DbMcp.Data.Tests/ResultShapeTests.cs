using System.Text.Json;
using DbMcp.Data.Models;
using DbMcp.Data.Services;

namespace DbMcp.Data.Tests;

/// <summary>
/// Asserts on the SERIALIZED JSON the model receives — the actual string a tool returns — for each
/// result/error projection, with NO live database. These guard the model-facing output contract that
/// the tool [Description]s promise; their absence is exactly why the execute_query array-vs-object
/// drift shipped uncaught.
/// </summary>
/// <remarks>
/// REACH — (b)-(e) drive <see cref="DatabaseService.ProjectResult"/> (made internal for this) via
/// <see cref="BatchRunResult"/> factory inputs, then run the output through the same
/// <see cref="DynamicSerializer"/> overload production uses.
/// <para>
/// (a1)-(a5) cover the execute_query read envelope. They assert the SERIALIZER over a hand-built
/// <c>{query, results:[{fields, rows, row_count}]}</c> dictionary — the exact graph
/// <see cref="DatabaseService.ExecuteQueryAsync"/> builds — run through
/// <see cref="DynamicSerializer.SerializeRead"/> (the production read path). COVERAGE BOUNDARY: the live
/// reader walk that produces that dictionary (GetDataTypeName types, Dapper <c>Parse()</c>,
/// <c>NextResultAsync</c>, both engines) runs only after ExecuteReaderAsync opens a connection, so it is
/// live-DB-smoke-only — NOT unit-reachable here. These tests pin the envelope shape + the null-cell
/// serialization gate; they do not pin the reader walk.
/// </para>
/// </remarks>
public class ResultShapeTests
{
    private static JsonElement SerializeAndParse(Dictionary<string, object?> result)
        => JsonDocument.Parse(DynamicSerializer.Serialize(result)).RootElement;

    private static JsonElement SerializeReadAndParse(Dictionary<string, object?> envelope)
        => JsonDocument.Parse(DynamicSerializer.SerializeRead(envelope)).RootElement;

    private static Dictionary<string, object?> Field(string name, string type)
        => new() { ["name"] = name, ["type"] = type };

    private static Dictionary<string, object?> ResultSet(
        List<Dictionary<string, object?>> fields, List<Dictionary<string, object?>> rows)
        => new() { ["fields"] = fields, ["rows"] = rows, ["row_count"] = rows.Count };

    // (a1) read envelope — root object, query echoed, results is an array of length 1 for a single set
    // (no top-level rows / returned_rows). The uniform single-set shape is still an array.
    [Fact]
    public void ReadEnvelope_SingleSet_IsObjectWithQueryAndResultsArray()
    {
        var envelope = new Dictionary<string, object?>
        {
            ["query"] = "SELECT id, email FROM users",
            ["results"] = new List<Dictionary<string, object?>>
            {
                ResultSet(
                    new List<Dictionary<string, object?>> { Field("id", "int4"), Field("email", "varchar") },
                    new List<Dictionary<string, object?>>
                    {
                        new() { ["id"] = 7, ["email"] = "a@x" }
                    })
            }
        };

        var root = SerializeReadAndParse(envelope);

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("SELECT id, email FROM users", root.GetProperty("query").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("results").ValueKind);
        Assert.Equal(1, root.GetProperty("results").GetArrayLength());
        Assert.False(root.TryGetProperty("rows", out _));
        Assert.False(root.TryGetProperty("returned_rows", out _));
    }

    // (a2) each result set carries fields[] + rows[] arrays and a numeric row_count == rows.length;
    // a field is an object with name + type string keys.
    [Fact]
    public void ReadEnvelope_ResultSet_HasFieldsRowsAndRowCount()
    {
        var envelope = new Dictionary<string, object?>
        {
            ["query"] = "SELECT id, email FROM users",
            ["results"] = new List<Dictionary<string, object?>>
            {
                ResultSet(
                    new List<Dictionary<string, object?>> { Field("id", "int4"), Field("email", "varchar") },
                    new List<Dictionary<string, object?>>
                    {
                        new() { ["id"] = 1, ["email"] = "a@x" },
                        new() { ["id"] = 2, ["email"] = "b@x" }
                    })
            }
        };

        var set = SerializeReadAndParse(envelope).GetProperty("results")[0];

        Assert.Equal(JsonValueKind.Array, set.GetProperty("fields").ValueKind);
        Assert.Equal(JsonValueKind.Array, set.GetProperty("rows").ValueKind);
        Assert.Equal(2, set.GetProperty("row_count").GetInt32());
        Assert.Equal(set.GetProperty("rows").GetArrayLength(), set.GetProperty("row_count").GetInt32());

        var firstField = set.GetProperty("fields")[0];
        Assert.Equal("id", firstField.GetProperty("name").GetString());
        Assert.Equal("int4", firstField.GetProperty("type").GetString());
    }

    // (a3) multi-set — two result sets produce results length 2, per-set row_count matches, order preserved.
    [Fact]
    public void ReadEnvelope_MultipleSets_PreservesOrderAndPerSetRowCount()
    {
        var envelope = new Dictionary<string, object?>
        {
            ["query"] = "SELECT id FROM users WHERE id = 7; SELECT count(*) AS n FROM orders",
            ["results"] = new List<Dictionary<string, object?>>
            {
                ResultSet(
                    new List<Dictionary<string, object?>> { Field("id", "int4") },
                    new List<Dictionary<string, object?>> { new() { ["id"] = 7 } }),
                ResultSet(
                    new List<Dictionary<string, object?>> { Field("n", "int8") },
                    new List<Dictionary<string, object?>>
                    {
                        new() { ["n"] = 3 },
                        new() { ["n"] = 4 }
                    })
            }
        };

        var results = SerializeReadAndParse(envelope).GetProperty("results");

        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("id", results[0].GetProperty("fields")[0].GetProperty("name").GetString());
        Assert.Equal(1, results[0].GetProperty("row_count").GetInt32());
        Assert.Equal("n", results[1].GetProperty("fields")[0].GetProperty("name").GetString());
        Assert.Equal(2, results[1].GetProperty("row_count").GetInt32());
    }

    // (a4) THE null-cell gate — a NULL row cell MUST serialize as JsonValueKind.Null with a PRESENT key
    // (not absent, not the string "DBNull"). This fails against the WhenWritingNull options and is the
    // reason SerializeRead exists.
    [Fact]
    public void ReadEnvelope_NullCell_SerializesAsPresentJsonNull()
    {
        var envelope = new Dictionary<string, object?>
        {
            ["query"] = "SELECT id, email FROM users WHERE id = 7",
            ["results"] = new List<Dictionary<string, object?>>
            {
                ResultSet(
                    new List<Dictionary<string, object?>> { Field("id", "int4"), Field("email", "varchar") },
                    new List<Dictionary<string, object?>>
                    {
                        new() { ["id"] = 7, ["email"] = null }
                    })
            }
        };

        var row = SerializeReadAndParse(envelope).GetProperty("results")[0].GetProperty("rows")[0];

        Assert.True(row.TryGetProperty("email", out var email));
        Assert.Equal(JsonValueKind.Null, email.ValueKind);
    }

    // (a5) empty set — a 0-row SELECT is a result set with populated fields[] and an empty rows[],
    // row_count 0 (NOT zero result sets).
    [Fact]
    public void ReadEnvelope_EmptySet_HasFieldsButEmptyRows()
    {
        var envelope = new Dictionary<string, object?>
        {
            ["query"] = "SELECT id, email FROM users WHERE 1 = 0",
            ["results"] = new List<Dictionary<string, object?>>
            {
                ResultSet(
                    new List<Dictionary<string, object?>> { Field("id", "int4"), Field("email", "varchar") },
                    new List<Dictionary<string, object?>>())
            }
        };

        var set = SerializeReadAndParse(envelope).GetProperty("results")[0];

        Assert.Equal(2, set.GetProperty("fields").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, set.GetProperty("rows").ValueKind);
        Assert.Equal(0, set.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, set.GetProperty("row_count").GetInt32());
    }

    // (b) execute_nonquery flat success — affected_rows + status.
    [Fact]
    public void NonquerySuccessShape_HasAffectedRowsAndStatus()
    {
        var run = BatchRunResult.Succeeded(new[]
        {
            new Dictionary<string, object?> { ["index"] = 0, ["affected_rows"] = 7 }
        });

        var root = SerializeAndParse(DatabaseService.ProjectResult(run, wasSplit: false, file: null));

        Assert.Equal(7, root.GetProperty("affected_rows").GetInt32());
        Assert.Equal("success", root.GetProperty("status").GetString());
    }

    // (c) split (batchSeparator) success — status + batches[] with index + affected_rows per item.
    [Fact]
    public void SplitSuccessShape_HasBatchesArrayWithIndexAndAffectedRows()
    {
        var run = BatchRunResult.Succeeded(new[]
        {
            new Dictionary<string, object?> { ["index"] = 0, ["affected_rows"] = 3 },
            new Dictionary<string, object?> { ["index"] = 1, ["affected_rows"] = 5 }
        });

        var root = SerializeAndParse(DatabaseService.ProjectResult(run, wasSplit: true, file: null));

        Assert.Equal("success", root.GetProperty("status").GetString());
        var batches = root.GetProperty("batches");
        Assert.Equal(JsonValueKind.Array, batches.ValueKind);
        Assert.Equal(2, batches.GetArrayLength());

        var first = batches[0];
        Assert.Equal(0, first.GetProperty("index").GetInt32());
        Assert.Equal(3, first.GetProperty("affected_rows").GetInt32());
    }

    // (d) atomic (useTransaction=true) failure — status:error + failed_batch_index + rolled_back:true.
    [Fact]
    public void AtomicFailureShape_HasFailedBatchIndexAndRolledBack()
    {
        var run = BatchRunResult.Failed(
            Array.Empty<Dictionary<string, object?>>(),
            failedIndex: 2, error: "constraint violation", rolledBack: true);

        var root = SerializeAndParse(DatabaseService.ProjectResult(run, wasSplit: true, file: null));

        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("failed_batch_index").GetInt32());
        Assert.True(root.GetProperty("rolled_back").GetBoolean());
    }

    // (e) non-transactional (useTransaction=false) split failure — per-batch committed flag present.
    [Fact]
    public void NonTransactionalFailureShape_SurfacesPerBatchCommittedFlag()
    {
        var run = BatchRunResult.Failed(
            new[]
            {
                new Dictionary<string, object?> { ["index"] = 0, ["affected_rows"] = 4, ["committed"] = true },
                new Dictionary<string, object?> { ["index"] = 1, ["error"] = "syntax error", ["committed"] = false }
            },
            failedIndex: 1, error: "syntax error", rolledBack: false);

        var root = SerializeAndParse(DatabaseService.ProjectResult(run, wasSplit: true, file: null));

        Assert.Equal("error", root.GetProperty("status").GetString());
        var batches = root.GetProperty("batches");
        Assert.True(batches[0].GetProperty("committed").GetBoolean());
        Assert.False(batches[1].GetProperty("committed").GetBoolean());
    }
}

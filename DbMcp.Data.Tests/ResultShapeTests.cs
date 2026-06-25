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
/// <see cref="DynamicSerializer"/> overload production uses. (a) cannot reach the inline
/// {rows, returned_rows} literal in ExecuteQueryAsync without opening a connection, so it asserts the
/// serializer's behavior over that documented shape: the Dictionary overload emits a JSON OBJECT with
/// both keys, never a bare array — which is the precise mechanism that contradicts the old
/// "JSON array" prose (Fix 1).
/// </remarks>
public class ResultShapeTests
{
    private static JsonElement SerializeAndParse(Dictionary<string, object?> result)
        => JsonDocument.Parse(DynamicSerializer.Serialize(result)).RootElement;

    // (a) execute_query — object with rows + returned_rows, NOT a bare array. Re-catches Fix 1.
    [Fact]
    public void ExecuteQueryShape_IsObjectWithRowsAndReturnedRows()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["name"] = "alice" },
            new() { ["id"] = 2, ["name"] = "bob" }
        };
        var queryResult = new Dictionary<string, object?>
        {
            ["rows"] = rows,
            ["returned_rows"] = rows.Count
        };

        var root = SerializeAndParse(queryResult);

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("rows").ValueKind);
        Assert.Equal(2, root.GetProperty("returned_rows").GetInt32());
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

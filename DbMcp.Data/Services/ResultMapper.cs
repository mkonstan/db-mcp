namespace DbMcp.Data.Services;

/// <summary>
/// Copies Dapper's <c>DapperRow</c> rows (from <c>QueryAsync</c> introspection paths and the
/// <see cref="DatabaseService.ExecuteQueryAsync"/> read walk) into plain
/// <c>Dictionary&lt;string, object?&gt;</c> instances that the serializer can emit directly.
/// </summary>
/// <remarks>
/// JOB — one materializer for BOTH the schema-introspection queries and the SELECT read path, so the
/// model-facing boundary type is uniform across every tool and there is no second copy loop in
/// <see cref="DatabaseService.ExecuteQueryAsync"/>.
/// <para>
/// WHY no <c>kvp.Value == DBNull.Value ? null</c> branch (the obvious-but-now-redundant fork a
/// maintainer might "restore" defensively): Dapper's DapperRow already converts a DBNull cell to C#
/// <c>null</c> before this code sees it (verified against the pinned Dapper 2.1.72 — a null cell comes
/// out as C# null, never DBNull). A cold reader re-adding the check would be dead defense; the read
/// serializer (<see cref="Models.DynamicSerializer"/>) is what guarantees that null then serializes as
/// JSON null rather than an absent key.
/// </para>
/// </remarks>
public static class ResultMapper
{
    public static List<Dictionary<string, object?>> ToSerializable(IEnumerable<dynamic> rows)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var kvp in (IDictionary<string, object>)row)
            {
                dict[kvp.Key] = kvp.Value;
            }
            result.Add(dict);
        }
        return result;
    }
}

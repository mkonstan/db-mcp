using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbMcp.Data.Models;

/// <summary>
/// Serializes the model-facing result dictionaries to JSON. Two option sets, deliberately split by
/// whether the payload is a WRITE shape or a READ result set.
/// </summary>
/// <remarks>
/// WHY two option sets — the write/introspection shapes (<see cref="Serialize(System.Collections.Generic.List{System.Collections.Generic.Dictionary{string, object?}})"/>
/// and <see cref="Serialize(System.Collections.Generic.Dictionary{string, object?})"/>) keep
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/> so an unset optional key (e.g. <c>file</c> on a
/// non-script run) drops out instead of emitting <c>null</c>. But <see cref="SerializeRead"/> MUST keep
/// null entries: a read row's NULL cell is data — the contract requires it to appear as a present key
/// with JSON <c>null</c> so the model can tell "column absent" from "value null". The rejected fork —
/// reusing the WhenWritingNull options for reads — would silently OMIT a null cell's key, the single
/// most dangerous quiet failure of the read contract. So the read path uses
/// <see cref="JsonIgnoreCondition.Never"/>.
/// </remarks>
public static class DynamicSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Serialize(List<Dictionary<string, object?>> data)
        => JsonSerializer.Serialize(data, Options);

    public static string Serialize(Dictionary<string, object?> data)
        => JsonSerializer.Serialize(data, Options);

    /// <summary>
    /// Serializes the uniform read envelope (<c>{query, results:[{fields, rows, row_count}]}</c>),
    /// preserving null row cells as present keys with JSON <c>null</c>. See the type remarks.
    /// </summary>
    public static string SerializeRead(Dictionary<string, object?> envelope)
        => JsonSerializer.Serialize(envelope, ReadOptions);
}

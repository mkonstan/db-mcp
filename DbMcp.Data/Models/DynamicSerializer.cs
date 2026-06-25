using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbMcp.Data.Models;

public static class DynamicSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(List<Dictionary<string, object?>> data)
        => JsonSerializer.Serialize(data, Options);

    public static string Serialize(Dictionary<string, object?> data)
        => JsonSerializer.Serialize(data, Options);
}

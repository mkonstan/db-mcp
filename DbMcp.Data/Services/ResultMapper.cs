namespace DbMcp.Data.Services;

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
                dict[kvp.Key] = kvp.Value == DBNull.Value ? null : kvp.Value;
            }
            result.Add(dict);
        }
        return result;
    }

    public static Dictionary<string, object?>? ToSerializableSingle(dynamic? row)
    {
        if (row == null) return null;
        var dict = new Dictionary<string, object?>();
        foreach (var kvp in (IDictionary<string, object>)row)
        {
            dict[kvp.Key] = kvp.Value == DBNull.Value ? null : kvp.Value;
        }
        return dict;
    }
}

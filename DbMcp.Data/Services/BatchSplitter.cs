using System.Text;
using System.Text.RegularExpressions;

namespace DbMcp.Data.Services;

/// <summary>
/// Splits SQL text into batches on lines that equal a caller-supplied token.
/// </summary>
/// <remarks>
/// JOB — given raw SQL and a separator token, return the ordered non-empty batches between
/// (and outside) the separator lines. A line is a separator only if, after trimming whitespace,
/// it equals the token EXACTLY (Ordinal, case-sensitive, nothing else on the line).
/// <para>
/// WHY pure text — this is deliberately NOT SQL-aware. Rejected fork: lexing comments and strings
/// so a token inside a `-- GO` comment or a `'GO'` literal would not split. That dialect-aware
/// lexer is exactly the silent-transform magic D13 / anti-drift #3 killed (cf. the deleted
/// WrapWithLimit). The accepted consequence: a token alone on a line inside a comment/string WILL
/// split there. Worst case is a loud syntax error and the caller picks a non-colliding token —
/// loud failure over silent corruption.
/// </para>
/// <para>
/// WHY <c>\r?\n</c> — splits on both CRLF and LF so a Windows-authored script and a Unix-authored
/// one batch identically. WHY Ordinal — case-sensitive and culture-invariant, the only correct
/// comparison for an arbitrary token (avoids the Turkish-I class of culture bugs).
/// </para>
/// <para>
/// This method does NOT enforce the "at least 2 batches" policy — it just returns whatever
/// non-empty segments exist. The policy check lives in <see cref="DatabaseService"/> so the error
/// message can name the tool parameter and this stays a clean pure function.
/// </para>
/// </remarks>
public static class BatchSplitter
{
    /// <summary>Splits <paramref name="sql"/> into non-empty batches on lines equal to <paramref name="separator"/>.</summary>
    /// <remarks>
    /// Precondition: the caller only invokes this when <paramref name="separator"/> is non-empty
    /// (the empty-separator no-split path is handled by the caller, not here). Whitespace-only and
    /// empty segments are dropped; the inner content of each retained segment is preserved verbatim.
    /// </remarks>
    public static IReadOnlyList<string> Split(string sql, string separator)
    {
        var lines = Regex.Split(sql, "\r?\n");
        var segments = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            if (string.Equals(line.Trim(), separator, StringComparison.Ordinal))
            {
                Flush(current, segments);
                current.Clear();
            }
            else
            {
                current.AppendLine(line);
            }
        }

        Flush(current, segments);
        return segments;
    }

    /// <summary>Appends the buffered segment to <paramref name="segments"/> unless it is empty or whitespace-only.</summary>
    private static void Flush(StringBuilder buffer, List<string> segments)
    {
        var segment = buffer.ToString();
        if (!string.IsNullOrWhiteSpace(segment))
            segments.Add(segment);
    }
}

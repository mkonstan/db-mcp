namespace DbMcp.Data.Services;

/// <summary>
/// Internal carrier between the shared batch-execution core and the two public methods that
/// project it into the serialized result shape. NEVER crosses the serialization boundary.
/// </summary>
/// <remarks>
/// JOB — separate <i>execution</i> (the shared <see cref="DatabaseService"/> core, which runs N
/// batches and knows whether they committed) from <i>shape projection</i> (the caller, which maps
/// to today's flat shape or the per-batch array based on separator-presence alone). The core
/// returns this; the caller owns the <c>Dictionary&lt;string,object?&gt;</c> mapping.
/// <para>
/// WHY this is NOT the serialized-record copy-layer anti-pattern (mcp-dotnet §10.4 / anti-drift #4):
/// the entries it carries are already the inline <c>Dictionary&lt;string,object?&gt;</c> values that
/// will be serialized — this record is a private execution result, not a parallel domain model that
/// gets hand-mapped onto the dictionaries. It exists so one core serves both the flat and the array
/// projection without the core needing to know which shape the caller wants.
/// </para>
/// </remarks>
internal sealed record BatchRunResult(
    IReadOnlyList<Dictionary<string, object?>> Entries,
    bool Success,
    int FailedIndex,
    string? Error,
    bool RolledBack)
{
    /// <summary>All batches ran; the caller projects <see cref="Entries"/> to the success shape.</summary>
    public static BatchRunResult Succeeded(IReadOnlyList<Dictionary<string, object?>> entries)
        => new(entries, Success: true, FailedIndex: -1, Error: null, RolledBack: false);

    /// <summary>A batch failed; the caller projects to the error shape using <paramref name="failedIndex"/> and <paramref name="rolledBack"/>.</summary>
    public static BatchRunResult Failed(
        IReadOnlyList<Dictionary<string, object?>> entries, int failedIndex, string error, bool rolledBack)
        => new(entries, Success: false, failedIndex, error, rolledBack);
}

/// <summary>
/// Tags the failing batch index onto a batch error raised inside the atomic (transactional)
/// execution path so the caller can shape the failure after the rollback has run.
/// </summary>
/// <remarks>
/// WHY a dedicated exception — under <c>useTransaction=true</c> a batch failure is an exception path
/// (the whole operation rolled back), but the caller still needs <i>which</i> batch failed to build
/// the <c>failed_batch_index</c> response. The thrown engine exception alone carries no index. This
/// is caught immediately OUTSIDE the Polly callback and converted to a failure
/// <see cref="BatchRunResult"/> — it is a reportable result, not a server fault, so it never
/// propagates as an unhandled throw.
/// </remarks>
internal sealed class BatchExecutionException(int failedIndex, Exception inner)
    : Exception(inner.Message, inner)
{
    /// <summary>Zero-based index of the batch that failed.</summary>
    public int FailedIndex { get; } = failedIndex;
}

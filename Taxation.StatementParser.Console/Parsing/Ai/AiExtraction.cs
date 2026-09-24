namespace Taxation.StatementParser.Console.Parsing.Ai;

/// <summary>
/// A single transaction row produced by the local AI extractor. Values mirror the canonical
/// statement columns. Missing cells are represented by empty strings; the extractor never invents
/// data. Debit/Credit/Balance are copied verbatim as the model read them (already digit-checked).
/// </summary>
public sealed record AiExtractionRow(
    string Date,
    string Description,
    string Debit,
    string Credit,
    string Balance);

/// <summary>
/// The outcome of a local AI extraction attempt. <see cref="Succeeded"/> is <c>false</c> whenever the
/// runtime was unavailable, timed out, returned unparyseable output, or produced no rows — in which
/// case the caller silently keeps the geometric parser result.
/// </summary>
public sealed class AiExtractionResult
{
    private AiExtractionResult(bool succeeded, IReadOnlyList<AiExtractionRow> rows, string? diagnostic)
    {
        Succeeded = succeeded;
        Rows = rows;
        Diagnostic = diagnostic;
    }

    /// <summary>True when the model returned at least one usable row.</summary>
    public bool Succeeded { get; }

    /// <summary>The extracted rows (empty when <see cref="Succeeded"/> is false).</summary>
    public IReadOnlyList<AiExtractionRow> Rows { get; }

    /// <summary>Optional human-readable reason for a failure (for the diagnostics log only).</summary>
    public string? Diagnostic { get; }

    public static AiExtractionResult Success(IReadOnlyList<AiExtractionRow> rows) =>
        new(rows.Count > 0, rows, null);

    public static AiExtractionResult Failure(string diagnostic) =>
        new(false, Array.Empty<AiExtractionRow>(), diagnostic);
}

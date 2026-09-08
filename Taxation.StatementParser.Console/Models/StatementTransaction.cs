namespace Taxation.StatementParser.Console.Models;

/// <summary>
/// Represents a single bank statement transaction.
/// Values are stored per column (keyed by the user supplied column name) so the parser
/// can support a dynamic, bank specific set of columns.
/// A transaction may span multiple physical lines in the PDF (multi line description);
/// continuation lines are merged into the relevant column via <see cref="AppendToColumn"/>.
/// </summary>
public sealed class StatementTransaction
{
    private readonly Dictionary<string, string> _cells;

    public StatementTransaction(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Columns = columns;
        _cells = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string column in columns)
        {
            _cells[column] = string.Empty;
        }
    }

    /// <summary>The ordered set of columns this transaction was parsed against.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the trimmed value for the given column, or an empty string.</summary>
    public string this[string column] =>
        _cells.TryGetValue(column, out string? value) ? value : string.Empty;

    /// <summary>Sets the initial value for a column (used when a new transaction row starts).</summary>
    public void SetColumn(string column, string value)
    {
        if (!_cells.ContainsKey(column))
        {
            return;
        }

        _cells[column] = (value ?? string.Empty).Trim();
    }

    /// <summary>
    /// Appends continuation text (from a wrapped line belonging to the same transaction)
    /// to an existing column value. Blank fragments are ignored so no stray whitespace is introduced.
    /// </summary>
    public void AppendToColumn(string column, string value)
    {
        if (!_cells.ContainsKey(column) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string existing = _cells[column];
        string fragment = value.Trim();
        _cells[column] = string.IsNullOrEmpty(existing)
            ? fragment
            : $"{existing} {fragment}";
    }

    /// <summary>True when every column is empty (used to discard blank lines).</summary>
    public bool IsEmpty => _cells.Values.All(string.IsNullOrWhiteSpace);
}

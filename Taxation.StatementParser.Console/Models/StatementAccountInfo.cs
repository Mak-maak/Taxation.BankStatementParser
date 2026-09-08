namespace Taxation.StatementParser.Console.Models;

/// <summary>
/// Account level metadata extracted from the top of a bank statement (before the transaction table).
/// This is surfaced on every worksheet so a reviewer can immediately see whose account the parsed
/// transactions belong to and the period they cover, without opening the original PDF.
/// </summary>
public sealed record StatementAccountInfo
{
    /// <summary>The account title / holder name (e.g. "MUHAMMAD AZIZ"), when present.</summary>
    public string? AccountTitle { get; init; }

    /// <summary>The account number (may be masked, e.g. "XXXXXX1234"), when present.</summary>
    public string? AccountNumber { get; init; }

    /// <summary>The International Bank Account Number, when the statement includes one.</summary>
    public string? Iban { get; init; }

    /// <summary>The start of the statement period, when it can be determined.</summary>
    public DateTime? FromDate { get; init; }

    /// <summary>The end of the statement period, when it can be determined.</summary>
    public DateTime? ToDate { get; init; }

    /// <summary>True when at least one piece of account metadata was found.</summary>
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(AccountTitle) ||
        !string.IsNullOrWhiteSpace(AccountNumber) ||
        !string.IsNullOrWhiteSpace(Iban) ||
        FromDate is not null ||
        ToDate is not null;

    /// <summary>A shared, empty instance used when no metadata could be extracted.</summary>
    public static StatementAccountInfo Empty { get; } = new();
}

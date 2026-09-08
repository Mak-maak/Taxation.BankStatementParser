using System.Globalization;
using System.Text.RegularExpressions;

namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// Validates and normalizes the anchor (Date) column of a bank statement.
///
/// For a financial product the Date column must be trustworthy, so this type is deliberately
/// conservative:
///   * It only accepts a value that BEGINS with a recognizable calendar date.
///   * It normalizes every accepted date to a single canonical format (<c>dd/MM/yyyy</c>).
///   * It rejects page footers / print stamps (e.g. "22 Aug 2026, 16:51") which carry a time
///     component, and rejects stray non-date text (e.g. "ANWAR", "HBL", "to").
///   * When a cell starts with a valid date but has trailing noise bled in from another column
///     (e.g. "03 Jul 2025 Money"), the trailing noise is returned separately so it can be moved
///     out of the Date column instead of corrupting it.
///
/// To be robust across the many layouts banks use, a wide range of input formats is accepted:
/// numeric (<c>01/07/2025</c>, <c>1-7-2025</c>, <c>01.07.25</c>), worded (<c>03 Jul 2025</c>,
/// <c>Jul 3, 2025</c>) and the abbreviated month style used by several core-banking exports such as
/// MCB (<c>02-JUN-25</c>, <c>02-Jun-2025</c>). Two-digit years are resolved by the current culture
/// calendar (e.g. <c>25</c> -&gt; <c>2025</c>). Every accepted value is still emitted in one single
/// canonical format so downstream formatting and accumulation stay consistent.
/// </summary>
public static class DateColumnNormalizer
{
    /// <summary>The single canonical output format used for every Date cell.</summary>
    public const string CanonicalFormat = "dd/MM/yyyy";

    // Accepted input date formats, covering numeric, worded and abbreviated-month styles with
    // '/', '-', '.' or space separators, and both two- and four-digit years. Two-digit years are
    // resolved by the calendar's TwoDigitYearMax (default: 1950-2049).
    private static readonly string[] AcceptedFormats =
    [
        // Numeric day/month/year (four-digit year).
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy",
        // Numeric day/month/year (two-digit year).
        "dd/MM/yy", "d/M/yy", "dd-MM-yy", "d-M-yy", "dd.MM.yy", "d.M.yy",
        // Abbreviated / full month, day first, space separated.
        "dd MMM yyyy", "d MMM yyyy", "dd MMMM yyyy", "d MMMM yyyy",
        "dd MMM yy", "d MMM yy", "dd MMMM yy", "d MMMM yy",
        // Abbreviated / full month, day first, '-' separated (e.g. MCB "02-JUN-25").
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MMM-yy", "d-MMM-yy",
        "dd-MMMM-yyyy", "d-MMMM-yyyy", "dd-MMMM-yy", "d-MMMM-yy",
        // Abbreviated month, day first, '/' or '.' separated.
        "dd/MMM/yyyy", "d/MMM/yyyy", "dd/MMM/yy", "d/MMM/yy",
        "dd.MMM.yyyy", "d.MMM.yyyy", "dd.MMM.yy", "d.MMM.yy",
        // Month first (e.g. "Jul 03 2025", comma already stripped before parsing).
        "MMM dd yyyy", "MMM d yyyy", "MMMM dd yyyy", "MMMM d yyyy",
        "MMM dd yy", "MMM d yy", "MMMM dd yy", "MMMM d yy",
    ];

    // A leading date token. Any of:
    //   numeric               01/07/2025, 1-7-2025, 01.07.25
    //   day-abbrevMonth-year  02-JUN-25, 02/Jun/2025, 02.Jun.25
    //   worded (day first)    03 Jul 2025, 3 July 25
    //   worded (month first)  Jul 03, 2025
    private static readonly Regex LeadingDateToken = new(
        @"^\s*(?<date>(\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4})|(\d{1,2}[/\-.][A-Za-z]{3,9}[/\-.]\d{2,4})|(\d{1,2}\s+[A-Za-z]{3,9}\s+\d{2,4})|([A-Za-z]{3,9}\s+\d{1,2},?\s+\d{2,4}))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A time component (e.g. "16:51") signals a footer/print stamp, never a transaction date cell.
    private static readonly Regex ContainsTime = new(
        @"\b\d{1,2}:\d{2}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to interpret <paramref name="rawCell"/> as a statement Date cell.
    /// </summary>
    /// <param name="rawCell">The raw text assigned to the anchor column for a line.</param>
    /// <param name="normalizedDate">The date normalized to <see cref="CanonicalFormat"/> when successful.</param>
    /// <param name="trailingNoise">
    /// Any text that followed the leading date within the same cell (e.g. "Money" in
    /// "03 Jul 2025 Money"). Empty when the cell was a clean date.
    /// </param>
    /// <returns><c>true</c> when the cell begins with a valid transaction date; otherwise <c>false</c>.</returns>
    public static bool TryNormalize(string? rawCell, out string normalizedDate, out string trailingNoise)
    {
        normalizedDate = string.Empty;
        trailingNoise = string.Empty;

        if (string.IsNullOrWhiteSpace(rawCell))
        {
            return false;
        }

        string cell = rawCell.Trim();

        // Reject footers / print timestamps outright: a transaction Date never carries a clock time.
        if (ContainsTime.IsMatch(cell))
        {
            return false;
        }

        Match match = LeadingDateToken.Match(cell);
        if (!match.Success)
        {
            return false;
        }

        string candidate = CollapseWhitespace(match.Groups["date"].Value).Replace(",", string.Empty);
        if (!DateTime.TryParseExact(
                candidate,
                AcceptedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            return false;
        }

        normalizedDate = parsed.ToString(CanonicalFormat, CultureInfo.InvariantCulture);
        trailingNoise = cell[match.Length..].Trim();
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> when the cell begins with a valid, normalizable transaction date.
    /// </summary>
    public static bool IsTransactionDate(string? rawCell) =>
        TryNormalize(rawCell, out _, out _);

    private static string CollapseWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();
}

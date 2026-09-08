using System.Globalization;
using Taxation.StatementParser.Console.Models;

namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// Infers the semantic <see cref="ColumnType"/> of each column from its heading and provides
/// robust, culture-tolerant conversion of the raw parsed text into a typed value.
///
/// Parsing is deliberately conservative: if a value cannot be interpreted with confidence the
/// original text is returned unchanged, so no information is ever lost from a financial statement.
/// </summary>
public static class ColumnClassifier
{
    private static readonly string[] DateKeywords = ["date"];

    private static readonly string[] AmountKeywords =
        ["debit", "credit", "balance", "amount", "money", "payment", "deposit", "withdrawal", "value", "in", "out"];

    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
        "MM/dd/yyyy", "M/d/yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd",
        "dd MMM yyyy", "d MMM yyyy", "dd MMMM yyyy", "d MMMM yyyy",
        "dd.MM.yyyy", "d.M.yyyy",
        "dd/MM/yy", "d/M/yy", "dd-MM-yy",
        "dd MMM", "d MMM"
    ];

    public static ColumnType Classify(string columnName)
    {
        string normalized = (columnName ?? string.Empty).ToLowerInvariant();

        // Date takes precedence: "Transaction Date" is a date even though it contains "transaction".
        if (ContainsWholeWord(normalized, DateKeywords))
        {
            return ColumnType.Date;
        }

        if (ContainsWholeWord(normalized, AmountKeywords))
        {
            return ColumnType.Amount;
        }

        return ColumnType.Text;
    }

    public static IReadOnlyList<ColumnType> ClassifyAll(IReadOnlyList<string> columns) =>
        columns.Select(Classify).ToList();

    /// <summary>Attempts to parse a monetary value, tolerating currency symbols, thousands
    /// separators, parentheses/trailing sign notation and CR/DR suffixes used by banks.</summary>
    public static bool TryParseAmount(string raw, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string text = raw.Trim();
        bool negative = false;

        // Parenthesised negatives: (1,234.56)
        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            negative = true;
            text = text[1..^1];
        }

        // Debit/Credit suffix notation, e.g. "1,234.56 DR" / "500.00 CR".
        string upper = text.ToUpperInvariant();
        if (upper.EndsWith("DR") || upper.EndsWith("CR"))
        {
            if (upper.EndsWith("DR"))
            {
                negative = true;
            }

            text = text[..^2].Trim();
        }

        // Strip currency symbols/letters and whitespace, keep digits, separators and signs.
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (char.IsDigit(ch) || ch is '.' or ',' or '-' or '+')
            {
                builder.Append(ch);
            }
        }

        string cleaned = builder.ToString();
        if (cleaned.Length == 0)
        {
            return false;
        }

        if (cleaned.StartsWith('-'))
        {
            negative = true;
            cleaned = cleaned.TrimStart('-', '+');
        }
        else
        {
            cleaned = cleaned.TrimStart('+');
        }

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
        {
            return false;
        }

        value = negative ? -parsed : parsed;
        return true;
    }

    /// <summary>Attempts to parse a date across common bank statement formats and cultures.</summary>
    public static bool TryParseDate(string raw, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string text = raw.Trim();

        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value))
        {
            return true;
        }

        return DateTime.TryParse(text, CultureInfo.CurrentCulture,
            DateTimeStyles.None, out value);
    }

    private static bool ContainsWholeWord(string normalizedName, string[] keywords)
    {
        string[] tokens = normalizedName.Split(
            [' ', '\t', '/', '-', '_', '.', '(', ')', '*', ':', '#', ',', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries);

        // Strip any remaining non-letter characters so decorated / OCR-noisy headings such as
        // "Date(DD/MM)" or "***Particulars******" still expose their keyword ("date", "particulars").
        return tokens
            .Select(token => new string(token.Where(char.IsLetter).ToArray()))
            .Any(token => token.Length > 0 && keywords.Contains(token));
    }
}

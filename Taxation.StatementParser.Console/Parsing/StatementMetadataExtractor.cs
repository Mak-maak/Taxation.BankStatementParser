using System.Globalization;
using System.Text.RegularExpressions;
using Taxation.StatementParser.Console.Models;

namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// Extracts account level metadata (account title, account number, IBAN and the statement period)
/// from the free text that appears above the transaction table of a bank statement.
///
/// Bank statements vary widely, so extraction is intentionally tolerant: it looks for common labels
/// ("Account Title", "Account Number", "IBAN", "Statement Period", ...) in a case and punctuation
/// insensitive way, and falls back to deriving the period from the parsed transaction dates when the
/// statement does not print an explicit period.
/// </summary>
public static partial class StatementMetadataExtractor
{
    private static readonly string[] TitleLabels =
        ["account title", "title of account", "account name", "name of account holder",
         "account holder name", "account holder", "customer name", "customer"];

    private static readonly string[] AccountNumberLabels =
        ["account number", "account no", "account #", "a/c number", "a/c no", "a/c #",
         "acct number", "acct no", "account"];

    // Labels that introduce a statement period expressed as a single range (both dates together).
    private static readonly string[] PeriodLabels =
        ["statement period", "statement duration", "balance duration", "account duration",
         "statement for the period", "for the period", "period from", "statement from",
         "duration", "period", "date range", "statement date"];

    // Labels that introduce ONLY the start date of the period.
    private static readonly string[] FromDateLabels =
        ["from date", "date from", "start date", "opening date", "period from", "from",
         "w.e.f", "with effect from", "since"];

    // Labels that introduce ONLY the end date of the period.
    private static readonly string[] ToDateLabels =
        ["to date", "date to", "end date", "closing date", "period to", "till date",
         "till", "upto", "up to", "to"];

    // Additional metadata column headers that commonly sit beside the account labels in a table
    // header row. Only used to recognise header rows, not to extract values.
    private static readonly string[] OtherHeaderLabels =
        ["iban", "scan code", "branch code", "branch name", "sort code", "swift", "bic", "currency"];

    // All recognised labels, used to detect a multi-column header row where several labels appear
    // side by side (so the trailing labels are not mistaken for a value).
    private static readonly string[] AllLabels =
        [.. TitleLabels, .. AccountNumberLabels, .. OtherHeaderLabels];

    /// <summary>
    /// Extracts the account metadata from the supplied statement lines (in reading order). When an
    /// explicit period is not present, <paramref name="transactions"/> and <paramref name="dateColumn"/>
    /// are used to derive the from/to dates from the transactions themselves.
    /// </summary>
    public static StatementAccountInfo Extract(
        IReadOnlyList<string> lines,
        IReadOnlyList<StatementTransaction> transactions,
        string? dateColumn)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(transactions);

        string? title = FindLabeledValue(lines, TitleLabels, IsNameValue, ExtractNamePortion);
        string? accountNumber = FindAccountNumber(lines);
        string? iban = FindIban(lines);

        // The account number label sometimes captures the IBAN; keep them distinct.
        if (!string.IsNullOrEmpty(iban) &&
            !string.IsNullOrEmpty(accountNumber) &&
            accountNumber.Replace(" ", string.Empty).Equals(iban, StringComparison.OrdinalIgnoreCase))
        {
            accountNumber = null;
        }

        (DateTime? from, DateTime? to) = FindPeriod(lines);
        if (from is null || to is null)
        {
            (DateTime? txnFrom, DateTime? txnTo) = DerivePeriodFromTransactions(transactions, dateColumn);
            from ??= txnFrom;
            to ??= txnTo;
        }

        return new StatementAccountInfo
        {
            AccountTitle = title,
            AccountNumber = accountNumber,
            Iban = iban,
            FromDate = from,
            ToDate = to,
        };
    }

    private static string? FindLabeledValue(
        IReadOnlyList<string> lines,
        string[] labels,
        Func<string, bool> isValid,
        Func<string, string>? select = null)
    {
        // Match longer labels first so "account number" wins over the shorter "account".
        string[] orderedLabels = [.. labels.OrderByDescending(l => l.Length)];

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string normalized = Normalize(line);

            foreach (string label in orderedLabels)
            {
                int position = normalized.IndexOf(label, StringComparison.Ordinal);
                if (position < 0)
                {
                    continue;
                }

                // Value on the same line, immediately after the label.
                string remainder = ExtractRemainder(line, label);
                if (!string.IsNullOrWhiteSpace(remainder) &&
                    !StartsWithKnownLabel(remainder))
                {
                    string candidate = select is null ? remainder : select(remainder);
                    if (isValid(candidate))
                    {
                        return CleanValue(candidate);
                    }
                }

                // Otherwise the value may sit on the following non-empty line.
                if (i + 1 < lines.Count)
                {
                    string next = lines[i + 1].Trim();
                    if (!string.IsNullOrWhiteSpace(next) && !LooksLikeLabelOnly(next))
                    {
                        // Reduce a multi-column data row to the relevant portion first, then validate,
                        // so trailing columns (account number, IBAN, ...) don't skew the check.
                        string candidate = select is null ? next : select(next);
                        if (isValid(candidate))
                        {
                            return CleanValue(candidate);
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the account number near any of the recognised account-number labels. Handles three
    /// layouts: the value on the same line as the label, on the next line, or in a TABLE where the
    /// label and its value sit a few rows apart (label row, then some other header/value rows, then
    /// the account number). Values that are actually dates (e.g. "9/3/2026") are rejected so a date
    /// printed next to the label is never mistaken for the account number.
    /// </summary>
    private static string? FindAccountNumber(IReadOnlyList<string> lines)
    {
        // How far below a label to keep scanning when the immediate line(s) do not hold a valid,
        // non-date account number (covers table layouts where the value is a few rows down).
        const int TableScanDepth = 6;

        string[] orderedLabels = [.. AccountNumberLabels.OrderByDescending(l => l.Length)];

        for (int i = 0; i < lines.Count; i++)
        {
            string normalized = Normalize(lines[i]);

            foreach (string label in orderedLabels)
            {
                if (normalized.IndexOf(label, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                // 1) Value on the same line, immediately after the label.
                string remainder = ExtractRemainder(lines[i], label);
                if (IsAccountValue(remainder))
                {
                    return CleanValue(FirstAccountToken(remainder));
                }

                // 2) HEADER format: the label sits on its own line and the value is printed on the
                //    immediately following non-empty line (e.g. "Account Title" / "ABDUL MUNTAQIM"
                //    or "Account Number" / "0013034377"). Prefer this over the wider table scan so an
                //    unrelated number further down the statement is never picked by mistake.
                string? nextLineValue = FindNextNonEmptyLine(lines, i, out int nextIndex);
                if (nextLineValue is not null &&
                    !LooksLikeLabelOnly(nextLineValue) &&
                    IsAccountValue(nextLineValue))
                {
                    return CleanValue(FirstAccountToken(nextLineValue));
                }

                // 3) Value on one of the following lines. In a table the account number may not be on
                //    the very next line, so scan a small window and take the first genuine account
                //    number token (skipping dates, labels and empty rows).
                int start = nextIndex >= 0 ? nextIndex : i + 1;
                int limit = Math.Min(lines.Count, i + 1 + TableScanDepth);
                for (int j = start; j < limit; j++)
                {
                    string candidate = lines[j].Trim();
                    if (string.IsNullOrWhiteSpace(candidate) || LooksLikeLabelOnly(candidate))
                    {
                        continue;
                    }

                    if (IsAccountValue(candidate))
                    {
                        return CleanValue(FirstAccountToken(candidate));
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the first non-empty line that follows <paramref name="labelIndex"/> along with its
    /// index. Blank rows (common in header/value layouts where a spacer separates the label from its
    /// value) are skipped. Returns <c>null</c> when no such line exists.
    /// </summary>
    private static string? FindNextNonEmptyLine(IReadOnlyList<string> lines, int labelIndex, out int index)
    {
        for (int i = labelIndex + 1; i < lines.Count; i++)
        {
            string candidate = lines[i].Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                index = i;
                return candidate;
            }
        }

        index = -1;
        return null;
    }

    /// <summary>
    /// Returns the text of a line that follows the given label, using the ORIGINAL casing so the
    /// extracted value keeps its real capitalisation.
    /// </summary>
    private static string ExtractRemainder(string originalLine, string normalizedLabel)
    {
        string normalized = Normalize(originalLine);
        int position = normalized.IndexOf(normalizedLabel, StringComparison.Ordinal);
        if (position < 0)
        {
            return string.Empty;
        }

        // Map the normalized position back onto the original line by walking word boundaries.
        // Simpler and robust: split the original line on the first label-terminating separator.
        int labelEnd = position + normalizedLabel.Length;

        // Rebuild an index into the original string by counting comparable characters.
        int originalIndex = MapNormalizedIndexToOriginal(originalLine, labelEnd);
        string remainder = originalIndex >= 0 && originalIndex <= originalLine.Length
            ? originalLine[originalIndex..]
            : string.Empty;

        return remainder.TrimStart(' ', ':', '-', '\t', '#', '.');
    }

    private static int MapNormalizedIndexToOriginal(string original, int normalizedIndex)
    {
        int normalizedCount = 0;
        for (int i = 0; i < original.Length; i++)
        {
            if (normalizedCount >= normalizedIndex)
            {
                return i;
            }

            char ch = original[i];
            if (char.IsLetterOrDigit(ch) || ch == ' ')
            {
                normalizedCount++;
            }
        }

        return original.Length;
    }

    private static (DateTime? From, DateTime? To) FindPeriod(IReadOnlyList<string> lines)
    {
        // 1) A labelled single-line range that contains two dates (e.g. "Statement Period: 01/01/2025 - 31/01/2025",
        //    "Balance Duration 01-Jan-2025 to 31-Jan-2025", "For the period 1 Jan 2025 30 Jan 2025").
        (DateTime? from, DateTime? to) = FindLabeledRange(lines);
        if (from is not null && to is not null)
        {
            return (from, to);
        }

        // 2) Separate "From Date" / "To Date" style labels which may appear on the same line,
        //    the next line, or as adjacent header/value columns.
        DateTime? fromDate = FindLabeledDate(lines, FromDateLabels);
        DateTime? toDate = FindLabeledDate(lines, ToDateLabels);
        from ??= fromDate;
        to ??= toDate;
        if (from is not null && to is not null)
        {
            return from <= to ? (from, to) : (to, from);
        }

        // 3) Any line that mentions a period-ish keyword and carries two dates, in any format.
        foreach (string line in lines)
        {
            string normalized = Normalize(line);
            if (!MentionsPeriodKeyword(normalized))
            {
                continue;
            }

            if (TryExtractTwoDates(line, out DateTime first, out DateTime second))
            {
                return first <= second ? (first, second) : (second, first);
            }
        }

        return (from, to);
    }

    /// <summary>
    /// Looks for a period label followed by (or preceding) two dates. The dates may be on the same
    /// line as the label or on the immediately following line, and separated by "to", "-", "–",
    /// "through", "and" or just whitespace.
    /// </summary>
    private static (DateTime? From, DateTime? To) FindLabeledRange(IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            string normalized = Normalize(lines[i]);
            if (!PeriodLabels.Any(label => normalized.Contains(label, StringComparison.Ordinal)))
            {
                continue;
            }

            // Same line as the label.
            if (TryExtractTwoDates(lines[i], out DateTime first, out DateTime second))
            {
                return first <= second ? (first, second) : (second, first);
            }

            // Value(s) may spill onto the following line.
            if (i + 1 < lines.Count &&
                TryExtractTwoDates(lines[i + 1], out DateTime nFirst, out DateTime nSecond))
            {
                return nFirst <= nSecond ? (nFirst, nSecond) : (nSecond, nFirst);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Finds a single date associated with any of the supplied labels. The date may be on the same
    /// line immediately after the label, or on the next non-empty line (supporting header/value
    /// column layouts where labels and values are printed on separate rows).
    /// </summary>
    private static DateTime? FindLabeledDate(IReadOnlyList<string> lines, string[] labels)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            string normalized = Normalize(lines[i]);

            foreach (string label in labels)
            {
                if (!ContainsLabelToken(normalized, label))
                {
                    continue;
                }

                // Prefer a date on the same line, after the label.
                string remainder = ExtractRemainder(lines[i], label);
                if (TryExtractFirstDate(remainder, out DateTime sameLine))
                {
                    return sameLine;
                }

                // Otherwise look at the following non-empty line.
                if (i + 1 < lines.Count && TryExtractFirstDate(lines[i + 1], out DateTime nextLine))
                {
                    return nextLine;
                }
            }
        }

        return null;
    }

    private static bool MentionsPeriodKeyword(string normalized) =>
        normalized.Contains("period") ||
        normalized.Contains("statement") ||
        normalized.Contains("duration") ||
        normalized.Contains("from") ||
        normalized.Contains("date");

    /// <summary>
    /// Ensures a short label such as "to" or "from" is matched as a whole word and not as a
    /// substring inside another word (e.g. "from" inside "chromium").
    /// </summary>
    private static bool ContainsLabelToken(string normalized, string label)
    {
        int index = 0;
        while ((index = normalized.IndexOf(label, index, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = index == 0 || !char.IsLetterOrDigit(normalized[index - 1]);
            int end = index + label.Length;
            bool rightOk = end >= normalized.Length || !char.IsLetterOrDigit(normalized[end]);
            if (leftOk && rightOk)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    private static bool TryExtractTwoDates(string text, out DateTime first, out DateTime second)
    {
        first = default;
        second = default;

        foreach (Match match in DateRegex().Matches(text))
        {
            if (!TryParseDate(match.Value, out DateTime date))
            {
                continue;
            }

            if (first == default)
            {
                first = date;
            }
            else
            {
                second = date;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractFirstDate(string text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (Match match in DateRegex().Matches(text))
        {
            if (TryParseDate(match.Value, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static (DateTime? From, DateTime? To) DerivePeriodFromTransactions(
        IReadOnlyList<StatementTransaction> transactions,
        string? dateColumn)
    {
        if (string.IsNullOrEmpty(dateColumn) || transactions.Count == 0)
        {
            return (null, null);
        }

        DateTime? min = null;
        DateTime? max = null;
        foreach (StatementTransaction transaction in transactions)
        {
            if (!ColumnClassifier.TryParseDate(transaction[dateColumn], out DateTime date))
            {
                continue;
            }

            if (min is null || date < min)
            {
                min = date;
            }

            if (max is null || date > max)
            {
                max = date;
            }
        }

        return (min, max);
    }

    private static string? FindIban(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            Match match = IbanRegex().Match(line.Replace(" ", string.Empty));
            if (match.Success)
            {
                return match.Value.ToUpperInvariant();
            }
        }

        return null;
    }

    private static bool IsNameValue(string value)
    {
        string trimmed = value.Trim();
        // A name must contain letters and should not be dominated by digits (which would be a number).
        return trimmed.Length is > 1 and < 100 &&
               trimmed.Any(char.IsLetter) &&
               trimmed.Count(char.IsDigit) <= trimmed.Length / 2;
    }

    /// <summary>
    /// Reduces a value coming from a data row to just the leading account-title portion. In table
    /// layouts several columns share one row (e.g. "ABDUL MUNTAQIM 0013034377 PK.. 12345"), so the
    /// name is taken up to the first token that starts a number, an account number or an IBAN.
    /// </summary>
    private static string ExtractNamePortion(string value)
    {
        string[] tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nameTokens = new List<string>();

        foreach (string token in tokens)
        {
            // Stop at the first token that is not name-like (contains a digit or looks like an
            // account/IBAN token), so trailing column values are dropped.
            if (token.Any(char.IsDigit) || IbanRegex().IsMatch(token))
            {
                break;
            }

            nameTokens.Add(token);
        }

        string name = string.Join(' ', nameTokens);
        return string.IsNullOrWhiteSpace(name) ? value : name;
    }

    private static bool IsAccountValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // A date printed next to the label (e.g. "9/3/2026") is never an account number.
        if (LooksLikeDate(value))
        {
            return false;
        }

        string token = FirstAccountToken(value);
        // Account numbers contain several digits and are a reasonable length (masked tokens included).
        return token.Length >= 5 &&
               token.Count(char.IsDigit) >= 4 &&
               !LooksLikeDate(token);
    }

    private static bool LooksLikeDate(string value)
    {
        string trimmed = value.Trim();
        return DateRegex().IsMatch(trimmed) || TryParseDate(trimmed, out _);
    }

    private static string FirstAccountToken(string value)
    {
        Match match = AccountTokenRegex().Match(value);
        return match.Success ? match.Value : string.Empty;
    }

    private static string CleanValue(string value) =>
        CollapseSpacesRegex().Replace(value, " ").Trim(' ', ':', '-', ',', '.', '\t', '#');

    private static bool LooksLikeLabelOnly(string line)
    {
        string normalized = Normalize(line);
        return normalized.EndsWith(" no", StringComparison.Ordinal) ||
               normalized.EndsWith("number", StringComparison.Ordinal) ||
               normalized.EndsWith("title", StringComparison.Ordinal) ||
               normalized.EndsWith("name", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the supplied text begins with one of the recognised metadata labels. This detects a
    /// multi-column HEADER row (e.g. "Account Number IBAN Scan Code" following an "Account Title"
    /// label on the same line) so the header labels are not mistaken for the actual value, which sits
    /// on the data row beneath the header.
    /// </summary>
    private static bool StartsWithKnownLabel(string text)
    {
        string normalized = Normalize(text);
        foreach (string label in AllLabels)
        {
            if (normalized.StartsWith(label, StringComparison.Ordinal))
            {
                int end = label.Length;
                bool wholeWord = end >= normalized.Length || !char.IsLetterOrDigit(normalized[end]);
                if (wholeWord)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Normalize(string value)
    {
        string lowered = (value ?? string.Empty).ToLowerInvariant();
        return CollapseSpacesRegex().Replace(lowered, " ").Trim();
    }

    private static bool TryParseDate(string raw, out DateTime value) =>
        ColumnClassifier.TryParseDate(raw, out value);

    // A masked or plain account token: digits mixed with optional mask characters and dash/space
    // separators. Slashes and dots are deliberately excluded so dates (e.g. 9/3/2026) never match.
    [GeneratedRegex(@"[X\*x0-9][X\*x0-9\- ]{3,}[X\*x0-9]")]
    private static partial Regex AccountTokenRegex();

    // IBAN: two letters, two check digits, then 11-30 alphanumerics (max total length 34).
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b", RegexOptions.IgnoreCase)]
    private static partial Regex IbanRegex();

    [GeneratedRegex(
        @"\b\d{1,2}[/\-\.]\d{1,2}[/\-\.]\d{2,4}\b|\b\d{4}[/\-\.]\d{1,2}[/\-\.]\d{1,2}\b|\b\d{1,2}[\s\-/](?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?[\s\-/]\d{2,4}\b|\b(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?[\s\-/]\d{1,2},?[\s\-/]\d{2,4}\b",
        RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseSpacesRegex();
}

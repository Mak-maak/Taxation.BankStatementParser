using System.Text.RegularExpressions;
using Taxation.StatementParser.Console.Models;

namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// The accumulation engine. This is a completely separate concern from statement parsing:
/// the <see cref="PdfStatementParser"/> produces the raw transactions, and this engine groups
/// them for the "Accumulated" view.
///
/// Grouping rule (kept intentionally simple and reliable for a financial product):
///   Two transactions accumulate together when they share the SAME masked account number
///   AND the SAME payee name.
///
/// A masked account number is always in a format such as <c>XXXXXXXABC09</c>: it mixes masking
/// characters / letters WITH digits (e.g. "XXXXXX1234", "PK36ABCD0001234567"). A plain date
/// (01/02/2025), a pure reference number (99213) or a timestamp is therefore never mistaken for
/// an account number, which was the previous defect.
/// </summary>
public static partial class AccumulationEngine
{
    private static readonly string[] DebitKeywords = ["debit", "withdrawal", "withdrawals", "paidout", "out", "dr", "payments", "payment"];
    private static readonly string[] CreditKeywords = ["credit", "deposit", "deposits", "paidin", "in", "cr", "received", "receipts"];
    private static readonly string[] ReferenceWords = ["ref", "reference", "refno", "txn", "trx", "trans", "no", "number", "id", "stan", "ft", "raast", "pos"];

    // When a statement has more than one date column (e.g. Transaction Date and Value Date), the
    // Transaction Date is always preferred as the effective date of the transaction.
    private static readonly string[] TransactionDateWords =
        ["transaction", "txn", "trans", "posting", "post", "booking", "book", "entry"];

    public static AccumulationResult Build(
        IReadOnlyList<string> columns,
        IReadOnlyList<ColumnType> types,
        IReadOnlyList<StatementTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(transactions);

        AccumulationRoles roles = ResolveRoles(columns, types);

        var order = new List<string>();
        var map = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        foreach (StatementTransaction transaction in transactions)
        {
            string description = transaction[roles.DescriptionColumn];
            string account = ExtractAccountNumber(description);
            string name = NormalizeName(description, account);

            // Group on masked account number + name. When there is no account number we fall back
            // to the name alone so identical payees still accumulate.
            string key = $"{name.ToUpperInvariant()}\u0001{account.ToUpperInvariant()}";

            if (!map.TryGetValue(key, out Accumulator? accumulator))
            {
                accumulator = new Accumulator(name, account);
                map[key] = accumulator;
                order.Add(key);
            }

            accumulator.Add(transaction, roles);
        }

        var groups = order.Select(key => map[key].ToGroup()).ToList();
        return new AccumulationResult(roles, groups);
    }

    internal static AccumulationRoles ResolveRoles(IReadOnlyList<string> columns, IReadOnlyList<ColumnType> types)
    {
        string? dateColumn = PreferredDateColumn(columns, types);
        string descriptionColumn = FirstOfType(columns, types, ColumnType.Text)
            ?? columns[0];

        string? debitColumn = FirstAmountMatching(columns, types, DebitKeywords);
        string? creditColumn = FirstAmountMatching(columns, types, CreditKeywords);

        return new AccumulationRoles(dateColumn, descriptionColumn, debitColumn, creditColumn);
    }

    /// <summary>
    /// Selects the effective date column. When several date columns exist the Transaction Date is
    /// preferred over any Value Date; otherwise the first non Value date column is used, falling back
    /// to the first date column of any kind.
    /// </summary>
    private static string? PreferredDateColumn(IReadOnlyList<string> columns, IReadOnlyList<ColumnType> types)
    {
        var dateColumns = new List<int>();
        for (int i = 0; i < columns.Count; i++)
        {
            if (types[i] == ColumnType.Date)
            {
                dateColumns.Add(i);
            }
        }

        if (dateColumns.Count == 0)
        {
            return null;
        }

        foreach (int index in dateColumns)
        {
            if (HeadingContainsAny(columns[index], TransactionDateWords))
            {
                return columns[index];
            }
        }

        foreach (int index in dateColumns)
        {
            if (!HeadingContainsAny(columns[index], ["value"]))
            {
                return columns[index];
            }
        }

        return columns[dateColumns[0]];
    }

    private static bool HeadingContainsAny(string heading, string[] keywords)
    {
        string[] tokens = heading
            .ToLowerInvariant()
            .Split([' ', '\t', '/', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);

        return tokens.Any(keywords.Contains);
    }

    /// <summary>
    /// Extracts the account identifier from a description. An account identifier is either a masked
    /// token that mixes letters / mask characters with digits (e.g. <c>XXXXXX1234</c>,
    /// <c>PK36ABCD0001234567</c>, <c>PYxxxx0000</c>) OR a long pure-digit account number
    /// (e.g. <c>0013034377</c>). Short per-transaction references such as a STAN <c>(652121)</c> are
    /// deliberately excluded so distinct payees are never merged and identical ones never split.
    /// Returns an empty string when none is present.
    /// </summary>
    internal static string ExtractAccountNumber(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        // Remove per-transaction references first so a STAN number can never be read as an account.
        string scannable = ReferenceTokenRegex().Replace(description, " ");

        string best = string.Empty;
        foreach (Match match in AccountTokenRegex().Matches(scannable))
        {
            string token = match.Value.Trim('-', '/', '.', ',', ':', ';', '*', '#');
            if (!IsAccountNumber(token))
            {
                continue;
            }

            // Prefer the longest qualifying token (account numbers are the longest such token).
            if (token.Length > best.Length)
            {
                best = token;
            }
        }

        return best;
    }

    /// <summary>
    /// True when the token is an account identifier: either a masked token (see
    /// <see cref="IsMaskedAccountNumber"/>) or a long pure-digit account number (>= 8 digits, which
    /// distinguishes a real account from a short STAN / reference number).
    /// </summary>
    internal static bool IsAccountNumber(string token)
    {
        if (IsMaskedAccountNumber(token))
        {
            return true;
        }

        // A long run of digits (>= 8) is a real account number, not a short reference.
        return token.Length >= 8 && token.All(char.IsDigit);
    }

    /// <summary>
    /// True when the token looks like a masked account number: it contains at least one digit AND
    /// at least one masking character or letter (X, x, *, A-Z), and is long enough (>= 6 characters)
    /// to be an account identifier rather than a short code.
    /// </summary>
    internal static bool IsMaskedAccountNumber(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 6)
        {
            return false;
        }

        bool hasDigit = false;
        bool hasLetterOrMask = false;
        foreach (char ch in token)
        {
            if (char.IsDigit(ch))
            {
                hasDigit = true;
            }
            else if (char.IsLetter(ch) || ch is '*')
            {
                hasLetterOrMask = true;
            }
            else if (ch is not ('-' or '/'))
            {
                // Any other punctuation disqualifies the token (dates use '.', spaces, etc.).
                return false;
            }
        }

        return hasDigit && hasLetterOrMask;
    }

    /// <summary>
    /// Derives the payee name by removing the account number and any dates from the description.
    /// Deliberately simple: no category guessing, so the result is predictable and reliable.
    /// </summary>
    internal static string NormalizeName(string description, string accountNumber)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "(Unknown)";
        }

        string working = description;

        if (!string.IsNullOrEmpty(accountNumber))
        {
            working = working.Replace(accountNumber, " ", StringComparison.OrdinalIgnoreCase);
        }

        working = DateRegex().Replace(working, " ");

        // Remove per-transaction reference tokens such as "STAN(956332)", "STAN 956332" or a bare
        // "(956332)". These differ on every transaction, so leaving them in would wrongly split the
        // same payee into many groups. Removing them is essential for reliable accumulation.
        working = ReferenceTokenRegex().Replace(working, " ");

        string collapsed = CollapseSpacesRegex().Replace(working, " ").Trim();

        // Strip trailing reference / numeric tokens (e.g. "... REF 1", "... 0012") so the same
        // payee accumulates regardless of a per-transaction reference number.
        string[] tokens = collapsed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int end = tokens.Length;
        while (end > 0 && IsTrailingNoise(tokens[end - 1]))
        {
            end--;
        }

        string normalized = string.Join(' ', tokens.Take(end))
            .Trim(' ', '-', ',', '.', ':', ';', '/', '*', '#');

        return normalized.Length == 0 ? "(Unknown)" : normalized;
    }

    private static bool IsTrailingNoise(string token)
    {
        string cleaned = token.Trim('-', ',', '.', ':', ';', '/', '*', '#');
        if (cleaned.Length == 0)
        {
            return true;
        }

        if (ReferenceWords.Contains(cleaned.ToLowerInvariant()))
        {
            return true;
        }

        // A trailing token that is purely numeric is a reference / sequence number.
        return cleaned.All(char.IsDigit);
    }

    private static string? FirstOfType(IReadOnlyList<string> columns, IReadOnlyList<ColumnType> types, ColumnType type)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (types[i] == type)
            {
                return columns[i];
            }
        }

        return null;
    }

    private static string? FirstAmountMatching(IReadOnlyList<string> columns, IReadOnlyList<ColumnType> types, string[] keywords)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (types[i] != ColumnType.Amount)
            {
                continue;
            }

            string[] tokens = columns[i]
                .ToLowerInvariant()
                .Split([' ', '\t', '/', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Any(keywords.Contains))
            {
                return columns[i];
            }
        }

        return null;
    }

    // A candidate token is a run of letters/digits/mask characters (optionally with '-' or '/'
    // separators). Whether it is really an account number is decided by IsMaskedAccountNumber.
    [GeneratedRegex(@"[A-Za-z0-9*][A-Za-z0-9*\-/]{4,}[A-Za-z0-9*]")]
    private static partial Regex AccountTokenRegex();

    [GeneratedRegex(
        @"\b\d{1,2}[/\-\.]\d{1,2}[/\-\.]\d{2,4}\b|\b\d{4}[/\-\.]\d{1,2}[/\-\.]\d{1,2}\b|\b\d{1,2}\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{2,4}\b",
        RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseSpacesRegex();

    // Matches per-transaction references such as "STAN(956332)", "STAN 956332", "REF(1234)",
    // "FT-99213" or a bare parenthesized number "(956332)". These vary per transaction and must be
    // stripped so the same payee accumulates reliably.
    [GeneratedRegex(
        @"\b(?:stan|ref|refno|reference|ft|trx|txn|trans|id)\b[\s:#\-]*\(?\d+\)?|\(\d{3,}\)",
        RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ReferenceTokenRegex();

    private sealed class Accumulator(string name, string account)
    {
        private readonly List<StatementTransaction> _transactions = [];
        private decimal _debit;
        private decimal _credit;
        private DateTime? _first;
        private DateTime? _last;

        public void Add(StatementTransaction transaction, AccumulationRoles roles)
        {
            _transactions.Add(transaction);

            if (roles.DebitColumn is not null &&
                ColumnClassifier.TryParseAmount(transaction[roles.DebitColumn], out decimal debit))
            {
                _debit += Math.Abs(debit);
            }

            if (roles.CreditColumn is not null &&
                ColumnClassifier.TryParseAmount(transaction[roles.CreditColumn], out decimal credit))
            {
                _credit += Math.Abs(credit);
            }

            if (roles.DateColumn is not null &&
                ColumnClassifier.TryParseDate(transaction[roles.DateColumn], out DateTime date))
            {
                if (_first is null || date < _first)
                {
                    _first = date;
                }

                if (_last is null || date > _last)
                {
                    _last = date;
                }
            }
        }

        public TransactionGroup ToGroup() => new()
        {
            Name = name,
            AccountNumber = account,
            TotalDebit = _debit,
            TotalCredit = _credit,
            FirstDate = _first,
            LastDate = _last,
            Transactions = _transactions
        };
    }
}

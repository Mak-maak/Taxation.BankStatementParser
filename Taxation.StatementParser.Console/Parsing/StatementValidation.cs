using Taxation.StatementParser.Console.Models;

namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// Lightweight sanity checks used to decide (a) whether the geometric parser result is good enough
/// to keep, and (b) whether an AI extraction result is trustworthy before accepting it.
/// <para>
/// The primary signal is running-balance continuity: for a correctly parsed statement, each row's
/// balance equals the previous balance plus the credit minus the debit. A high proportion of rows
/// satisfying this relationship indicates the amount/balance columns were read correctly.
/// </para>
/// </summary>
public static class StatementValidation
{
    /// <summary>Absolute tolerance (currency units) when comparing computed vs. printed balances.</summary>
    private const decimal BalanceTolerance = 0.05m;

    /// <summary>
    /// Returns true when the transactions look structurally sound: there is at least one row, and —
    /// when Debit/Credit/Balance columns are present — the running balance reconciles for the large
    /// majority of consecutive row pairs.
    /// </summary>
    public static bool LooksValid(
        IReadOnlyList<string> columns,
        IReadOnlyList<StatementTransaction> transactions)
    {
        if (transactions is null || transactions.Count == 0)
        {
            return false;
        }

        string? debitColumn = FindColumn(columns, ColumnType.Amount, "debit", "withdrawal");
        string? creditColumn = FindColumn(columns, ColumnType.Amount, "credit", "deposit");
        string? balanceColumn = FindColumn(columns, ColumnType.Amount, "balance");

        // Without a balance column we cannot reconcile; accept a non-empty result as-is.
        if (balanceColumn is null || (debitColumn is null && creditColumn is null))
        {
            return true;
        }

        double ratio = BalanceReconciliationRatio(transactions, debitColumn, creditColumn, balanceColumn);

        // With too few comparable rows, don't penalize; otherwise require a strong majority to match.
        return ratio < 0 || ratio >= 0.7;
    }

    /// <summary>
    /// Fraction (0..1) of consecutive row pairs whose running balance reconciles. Returns -1 when
    /// there are not enough rows with parseable balances to make a meaningful judgement.
    /// </summary>
    public static double BalanceReconciliationRatio(
        IReadOnlyList<StatementTransaction> transactions,
        string? debitColumn,
        string? creditColumn,
        string balanceColumn)
    {
        int comparable = 0;
        int matched = 0;
        decimal? previousBalance = null;

        foreach (StatementTransaction transaction in transactions)
        {
            if (!ColumnClassifier.TryParseAmount(transaction[balanceColumn], out decimal balance))
            {
                previousBalance = null;
                continue;
            }

            if (previousBalance is decimal prev)
            {
                decimal debit = ParseAmountOrZero(transaction, debitColumn);
                decimal credit = ParseAmountOrZero(transaction, creditColumn);

                comparable++;
                decimal expected = prev + credit - debit;
                if (Math.Abs(expected - balance) <= BalanceTolerance)
                {
                    matched++;
                }
            }

            previousBalance = balance;
        }

        return comparable == 0 ? -1 : (double)matched / comparable;
    }

    private static decimal ParseAmountOrZero(StatementTransaction transaction, string? column)
    {
        if (column is null)
        {
            return 0m;
        }

        return ColumnClassifier.TryParseAmount(transaction[column], out decimal value) ? value : 0m;
    }

    private static string? FindColumn(
        IReadOnlyList<string> columns,
        ColumnType requiredType,
        params string[] keywords)
    {
        foreach (string column in columns)
        {
            if (ColumnClassifier.Classify(column) != requiredType)
            {
                continue;
            }

            string lower = column.ToLowerInvariant();
            foreach (string keyword in keywords)
            {
                if (lower.Contains(keyword, StringComparison.Ordinal))
                {
                    return column;
                }
            }
        }

        return null;
    }
}

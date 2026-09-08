namespace Taxation.StatementParser.Console.Models;

/// <summary>
/// An accumulated set of transactions that share the same masked account number and payee name.
/// Carries the aggregate figures shown on the summary row plus the underlying transactions that
/// make up the group (the "bifurcation" shown as expandable detail rows in Excel).
/// </summary>
public sealed class TransactionGroup
{
    public required string Name { get; init; }

    public required string AccountNumber { get; init; }

    public decimal TotalDebit { get; init; }

    public decimal TotalCredit { get; init; }

    /// <summary>Net movement for the group (credits minus debits).</summary>
    public decimal Net => TotalCredit - TotalDebit;

    public DateTime? FirstDate { get; init; }

    public DateTime? LastDate { get; init; }

    public required IReadOnlyList<StatementTransaction> Transactions { get; init; }

    public int Count => Transactions.Count;
}

/// <summary>The column names resolved to their semantic role for accumulation.</summary>
public sealed record AccumulationRoles(string? DateColumn, string DescriptionColumn, string? DebitColumn, string? CreditColumn);

/// <summary>The outcome of building an accumulation: the resolved roles and the ordered groups.</summary>
public sealed record AccumulationResult(AccumulationRoles Roles, IReadOnlyList<TransactionGroup> Groups);

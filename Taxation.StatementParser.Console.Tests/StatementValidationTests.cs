using FluentAssertions;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class StatementValidationTests
{
    private static readonly string[] Columns = ["Date", "Description", "Debit", "Credit", "Balance"];

    private static StatementTransaction Row(
        string date,
        string description,
        string debit,
        string credit,
        string balance)
    {
        var t = new StatementTransaction(Columns);
        t.SetColumn("Date", date);
        t.SetColumn("Description", description);
        t.SetColumn("Debit", debit);
        t.SetColumn("Credit", credit);
        t.SetColumn("Balance", balance);
        return t;
    }

    [Fact]
    public void LooksValid_WithNullOrEmpty_ShouldBeFalse()
    {
        StatementValidation.LooksValid(Columns, []).Should().BeFalse();
        StatementValidation.LooksValid(Columns, null!).Should().BeFalse();
    }

    [Fact]
    public void LooksValid_WithReconcilingBalances_ShouldBeTrue()
    {
        var transactions = new[]
        {
            Row("01/01/2025", "Opening balance", "", "", "1,000.00"),
            Row("02/01/2025", "Card payment", "50.00", "", "950.00"),
            Row("03/01/2025", "Salary", "", "2,000.00", "2,950.00"),
            Row("04/01/2025", "Rent", "1,200.00", "", "1,750.00"),
        };

        StatementValidation.LooksValid(Columns, transactions).Should().BeTrue();
    }

    [Fact]
    public void LooksValid_WithGarbledBalances_ShouldBeFalse()
    {
        // Balances do not reconcile with debits/credits at all.
        var transactions = new[]
        {
            Row("01/01/2025", "Opening balance", "", "", "1,000.00"),
            Row("02/01/2025", "Card payment", "50.00", "", "12.34"),
            Row("03/01/2025", "Salary", "", "2,000.00", "999.99"),
            Row("04/01/2025", "Rent", "1,200.00", "", "42.00"),
        };

        StatementValidation.LooksValid(Columns, transactions).Should().BeFalse();
    }

    [Fact]
    public void LooksValid_WithoutBalanceColumn_ShouldAcceptNonEmpty()
    {
        string[] columns = ["Date", "Description"];
        var t = new StatementTransaction(columns);
        t.SetColumn("Date", "01/01/2025");
        t.SetColumn("Description", "Something");

        StatementValidation.LooksValid(columns, [t]).Should().BeTrue();
    }

    [Fact]
    public void BalanceReconciliationRatio_WithNoComparableRows_ShouldReturnMinusOne()
    {
        var single = new[] { Row("01/01/2025", "Only row", "", "", "1,000.00") };

        StatementValidation
            .BalanceReconciliationRatio(single, "Debit", "Credit", "Balance")
            .Should().Be(-1);
    }

    [Fact]
    public void BalanceReconciliationRatio_WithPerfectStatement_ShouldReturnOne()
    {
        var transactions = new[]
        {
            Row("01/01/2025", "Opening", "", "", "1,000.00"),
            Row("02/01/2025", "Debit", "50.00", "", "950.00"),
            Row("03/01/2025", "Credit", "", "100.00", "1,050.00"),
        };

        StatementValidation
            .BalanceReconciliationRatio(transactions, "Debit", "Credit", "Balance")
            .Should().Be(1.0);
    }
}

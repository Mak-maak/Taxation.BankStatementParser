using FluentAssertions;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class ColumnClassifierTests
{
    [Theory]
    [InlineData("Booking Date", ColumnType.Date)]
    [InlineData("Transaction Date", ColumnType.Date)]
    [InlineData("date", ColumnType.Date)]
    [InlineData("Value Date", ColumnType.Date)]
    [InlineData("Transaction", ColumnType.Text)]
    [InlineData("Description", ColumnType.Text)]
    [InlineData("Reference", ColumnType.Text)]
    [InlineData("Debit", ColumnType.Amount)]
    [InlineData("Credit", ColumnType.Amount)]
    [InlineData("Available Balance", ColumnType.Amount)]
    [InlineData("Money In", ColumnType.Amount)]
    [InlineData("Money Out", ColumnType.Amount)]
    [InlineData("Amount", ColumnType.Amount)]
    [InlineData("Withdrawal", ColumnType.Amount)]
    [InlineData("Deposit", ColumnType.Amount)]
    public void Classify_ShouldReturnExpectedType(string columnName, ColumnType expected)
    {
        ColumnClassifier.Classify(columnName).Should().Be(expected);
    }

    [Fact]
    public void Classify_WhenNameIsNullOrEmpty_ShouldReturnText()
    {
        ColumnClassifier.Classify(string.Empty).Should().Be(ColumnType.Text);
        ColumnClassifier.Classify(null!).Should().Be(ColumnType.Text);
    }

    [Fact]
    public void ClassifyAll_ShouldPreserveOrder()
    {
        string[] columns = ["Transaction Date", "Description", "Debit", "Credit", "Available Balance"];

        IReadOnlyList<ColumnType> result = ColumnClassifier.ClassifyAll(columns);

        result.Should().Equal(
            ColumnType.Date, ColumnType.Text, ColumnType.Amount, ColumnType.Amount, ColumnType.Amount);
    }

    [Theory]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1234.56", 1234.56)]
    [InlineData("0.00", 0)]
    [InlineData("500.00 CR", 500.00)]
    [InlineData("1,234.56 DR", -1234.56)]
    [InlineData("(1,234.56)", -1234.56)]
    [InlineData("-42.10", -42.10)]
    [InlineData("+42.10", 42.10)]
    [InlineData("£1,000.00", 1000.00)]
    [InlineData("$2,500.75", 2500.75)]
    [InlineData("  3,000.00  ", 3000.00)]
    public void TryParseAmount_WhenValid_ShouldParse(string raw, double expected)
    {
        bool success = ColumnClassifier.TryParseAmount(raw, out decimal value);

        success.Should().BeTrue();
        value.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("N/A")]
    [InlineData("abc")]
    [InlineData("CR")]
    public void TryParseAmount_WhenInvalid_ShouldReturnFalse(string? raw)
    {
        bool success = ColumnClassifier.TryParseAmount(raw!, out decimal value);

        success.Should().BeFalse();
        value.Should().Be(0m);
    }

    [Theory]
    [InlineData("31/01/2025", 2025, 1, 31)]
    [InlineData("1/2/2025", 2025, 2, 1)]
    [InlineData("2025-03-15", 2025, 3, 15)]
    [InlineData("15 Jan 2025", 2025, 1, 15)]
    [InlineData("05.06.2025", 2025, 6, 5)]
    public void TryParseDate_WhenValid_ShouldParse(string raw, int year, int month, int day)
    {
        bool success = ColumnClassifier.TryParseDate(raw, out DateTime value);

        success.Should().BeTrue();
        value.Year.Should().Be(year);
        value.Month.Should().Be(month);
        value.Day.Should().Be(day);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a date")]
    public void TryParseDate_WhenInvalid_ShouldReturnFalse(string? raw)
    {
        ColumnClassifier.TryParseDate(raw!, out _).Should().BeFalse();
    }
}

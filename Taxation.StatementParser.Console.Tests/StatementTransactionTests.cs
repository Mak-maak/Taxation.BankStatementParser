using FluentAssertions;
using Taxation.StatementParser.Console.Models;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class StatementTransactionTests
{
    private static readonly string[] Columns = ["Date", "Description", "Debit", "Credit", "Balance"];

    [Fact]
    public void NewTransaction_ShouldStartEmpty()
    {
        var transaction = new StatementTransaction(Columns);

        transaction.IsEmpty.Should().BeTrue();
        transaction["Date"].Should().BeEmpty();
    }

    [Fact]
    public void SetColumn_ShouldTrimAndStoreValue()
    {
        var transaction = new StatementTransaction(Columns);

        transaction.SetColumn("Description", "  Salary payment  ");

        transaction["Description"].Should().Be("Salary payment");
        transaction.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void SetColumn_WithUnknownColumn_ShouldBeIgnored()
    {
        var transaction = new StatementTransaction(Columns);

        transaction.SetColumn("Unknown", "value");

        transaction["Unknown"].Should().BeEmpty();
        transaction.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AppendToColumn_ShouldMergeContinuationWithSpace()
    {
        var transaction = new StatementTransaction(Columns);
        transaction.SetColumn("Description", "Direct debit");

        transaction.AppendToColumn("Description", "utility company ref 12345");

        transaction["Description"].Should().Be("Direct debit utility company ref 12345");
    }

    [Fact]
    public void AppendToColumn_WhenExistingEmpty_ShouldSetValue()
    {
        var transaction = new StatementTransaction(Columns);

        transaction.AppendToColumn("Description", "continuation");

        transaction["Description"].Should().Be("continuation");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AppendToColumn_WithBlankFragment_ShouldBeIgnored(string? fragment)
    {
        var transaction = new StatementTransaction(Columns);
        transaction.SetColumn("Description", "original");

        transaction.AppendToColumn("Description", fragment!);

        transaction["Description"].Should().Be("original");
    }

    [Fact]
    public void Indexer_WithUnknownColumn_ShouldReturnEmpty()
    {
        var transaction = new StatementTransaction(Columns);

        transaction["DoesNotExist"].Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithNullColumns_ShouldThrow()
    {
        Action act = () => _ = new StatementTransaction(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

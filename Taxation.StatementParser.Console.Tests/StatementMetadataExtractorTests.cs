using FluentAssertions;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class StatementMetadataExtractorTests
{
    private static readonly StatementTransaction[] NoTransactions = [];

    [Fact]
    public void Extract_ShouldReadTitleAccountAndIban_FromLabelledLines()
    {
        string[] lines =
        [
            "Account Title: MUHAMMAD AZIZ",
            "Account Number: 0013034377",
            "IBAN: GB29 NWBK 6016 1331 9268 19",
        ];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.AccountTitle.Should().Be("MUHAMMAD AZIZ");
        info.AccountNumber.Should().Be("0013034377");
        info.Iban.Should().Be("GB29NWBK60161331926819");
    }

    [Fact]
    public void Extract_ShouldNotTreatDateNextToLabel_AsAccountNumber()
    {
        // In some statements a date is printed next to the "Account Number" label (table layout).
        // The real account number appears elsewhere; a date like "9/3/2026" must never be picked.
        string[] lines =
        [
            "Account Number    9/3/2026",
        ];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.AccountNumber.Should().BeNull();
    }

    [Fact]
    public void Extract_ShouldReadAccountNumber_FromTableWhereValueIsRowsBelowLabel()
    {
        // Table layout: the "Account Number" label row is followed by an unrelated date cell, and
        // the genuine account number sits a few rows further down the same column.
        string[] lines =
        [
            "Account Number    9/3/2026",
            "Statement Date",
            "Currency",
            "0013034377",
        ];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.AccountNumber.Should().Be("0013034377");
    }

    [Fact]
    public void Extract_ShouldNotTreatFollowingColumnHeaders_AsAccountTitle()
    {
        // Table layout where several labels share one header row and the data sits on the row below:
        //   "Account Title  Account Number  IBAN  Scan Code"
        //   "ABDUL MUNTAQIM 0013034377      PK.. 12345"
        // The title must be "ABDUL MUNTAQIM", not the trailing column headers.
        string[] lines =
        [
            "Account Title    Account Number    IBAN    Scan Code",
            "ABDUL MUNTAQIM   0013034377   PK36SCBL0000001234567890   12345",
        ];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.AccountTitle.Should().Be("ABDUL MUNTAQIM");
        info.AccountNumber.Should().Be("0013034377");
    }

    [Fact]
    public void Extract_ShouldReadAccountNumber_FromHeaderFormatWhereValueIsOnNextLine()
    {
        // Header layout: the label is printed on its own line and the value on the line beneath it
        // (e.g. "Account Title" over "ABDUL MUNTAQIM"). The account number follows the same pattern.
        string[] lines =
        [
            "Account Title",
            "ABDUL MUNTAQIM",
            "Account Number",
            "0013034377",
        ];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.AccountTitle.Should().Be("ABDUL MUNTAQIM");
        info.AccountNumber.Should().Be("0013034377");
    }

    [Fact]
    public void Extract_ShouldReadAccountNumber_FromHeaderFormatWithBlankSpacerLine()
    {
        // Header layout with a blank spacer row between the label and its value.
        string[] lines =
        [
            "Account Number",
            "",
            "0013034377",
            "Some other number 99887766",
        ];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.AccountNumber.Should().Be("0013034377");
    }

    [Fact]
    public void Extract_ShouldReadPeriod_FromSingleLineRangeWithStatementPeriodLabel()
    {
        string[] lines = ["Statement Period: 01/01/2025 - 31/01/2025"];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.FromDate.Should().Be(new DateTime(2025, 1, 1));
        info.ToDate.Should().Be(new DateTime(2025, 1, 31));
    }

    [Fact]
    public void Extract_ShouldReadPeriod_FromBalanceDurationLabelWithToKeyword()
    {
        string[] lines = ["Balance Duration 01-Jan-2025 to 31-Jan-2025"];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.FromDate.Should().Be(new DateTime(2025, 1, 1));
        info.ToDate.Should().Be(new DateTime(2025, 1, 31));
    }

    [Fact]
    public void Extract_ShouldReadPeriod_FromSeparateFromDateAndToDateLabelsOnSameLine()
    {
        string[] lines = ["From Date: 05/02/2025      To Date: 28/02/2025"];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.FromDate.Should().Be(new DateTime(2025, 2, 5));
        info.ToDate.Should().Be(new DateTime(2025, 2, 28));
    }

    [Fact]
    public void Extract_ShouldReadPeriod_FromSeparateFromToLabelsOnDifferentLines()
    {
        string[] lines =
        [
            "From Date",
            "01/03/2025",
            "To Date",
            "31/03/2025",
        ];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.FromDate.Should().Be(new DateTime(2025, 3, 1));
        info.ToDate.Should().Be(new DateTime(2025, 3, 31));
    }

    [Fact]
    public void Extract_ShouldReadPeriod_WhenBothDatesAreOnOneLineWithoutSeparator()
    {
        string[] lines = ["Statement for the period 1 Jan 2025 30 Jan 2025"];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        info.FromDate.Should().Be(new DateTime(2025, 1, 1));
        info.ToDate.Should().Be(new DateTime(2025, 1, 30));
    }

    [Fact]
    public void Extract_ShouldNotMatchFromKeyword_InsideUnrelatedWords()
    {
        string[] lines = ["Chromium report generated 15/04/2025"];

        var info = StatementMetadataExtractor.Extract(lines, NoTransactions, dateColumn: null);

        // "from" appears inside "Chromium" but must not be treated as a From-date label.
        info.FromDate.Should().BeNull();
        info.ToDate.Should().BeNull();
    }
}

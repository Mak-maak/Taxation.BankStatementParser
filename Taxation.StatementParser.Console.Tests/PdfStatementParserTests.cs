using FluentAssertions;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class PdfStatementParserTests
{
    private static readonly string[] Columns = ["Date", "Description", "Debit", "Credit", "Balance"];

    // Approximate X positions of each column in the generated PDF.
    private const double DateX = 50;
    private const double DescriptionX = 130;
    private const double DebitX = 330;
    private const double CreditX = 410;
    private const double BalanceX = 490;

    [Fact]
    public void Parse_ShouldExtractTransactions_AndMergeMultiLineDescription()
    {
        string path = CreateStatementPdf();
        try
        {
            var parser = new PdfStatementParser(Columns);

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            transactions.Should().HaveCount(3);

            transactions[0]["Date"].Should().Be("01/01/2025");
            transactions[0]["Description"].Should().Be("Opening balance");
            transactions[0]["Balance"].Should().Be("1,000.00");

            // Second transaction has a wrapped (multi-line) description that must be merged into one.
            transactions[1]["Date"].Should().Be("02/01/2025");
            transactions[1]["Description"].Should().Be("Card payment Amazon UK");
            transactions[1]["Debit"].Should().Be("50.00");

            transactions[2]["Date"].Should().Be("03/01/2025");
            transactions[2]["Credit"].Should().Be("2,000.00");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_WhenHeaderNotFound_ShouldThrow()
    {
        string path = CreateStatementPdf();
        try
        {
            var parser = new PdfStatementParser(["Nonexistent", "Columns", "Here"]);

            Action act = () => parser.Parse(path);

            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_WhenFileMissing_ShouldThrowFileNotFound()
    {
        var parser = new PdfStatementParser(Columns);

        Action act = () => parser.Parse(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf"));

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Parse_WithPasswordOnUnprotectedPdf_ShouldStillParse()
    {
        // Supplying a password for a PDF that is not encrypted must not break parsing: the parser
        // also tries the empty password, so a normal statement continues to work.
        string path = CreateStatementPdf();
        try
        {
            var parser = new PdfStatementParser(Columns, anchorColumnIndex: 0, log: null, password: "irrelevant");

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            transactions.Should().HaveCount(3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_WithBlankPath_ShouldThrowArgumentException(string path)
    {
        var parser = new PdfStatementParser(Columns);

        Action act = () => parser.Parse(path);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithFewerThanTwoColumns_ShouldThrow()
    {
        Action act = () => _ = new PdfStatementParser(["OnlyOne"]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithInvalidAnchorIndex_ShouldThrow()
    {
        Action act = () => _ = new PdfStatementParser(Columns, anchorColumnIndex: 99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Parse_ShouldRejectPageFooters_AndNormalizeBledInDates()
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.A4);

        AddRow(page, font, 800, "Date", "Description", "Debit", "Credit", "Balance");
        // Genuine transaction with a worded date.
        AddRow(page, font, 770, "02/07/2025", "Card payment", "50.00", null, "950.00");
        // Date cell with description text bled in ("03 Jul 2025 Money").
        AddText(page, font, DateX, 745, "03 Jul 2025 Money");
        AddText(page, font, BalanceX, 745, "900.00");
        // Page footer: page number + print stamp sitting in the date column region.
        AddText(page, font, DateX, 60, "1 22 Aug 2026, 16:51");
        // Stray text (e.g. a name) in the date column region.
        AddText(page, font, DateX, 50, "ANWAR");

        byte[] bytes = builder.Build();
        string path = Path.Combine(Path.GetTempPath(), $"statement_{Guid.NewGuid()}.pdf");
        File.WriteAllBytes(path, bytes);

        try
        {
            var parser = new PdfStatementParser(Columns);

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            // Only the two genuine transactions survive; footer + stray text are discarded.
            transactions.Should().HaveCount(2);

            transactions[0]["Date"].Should().Be("02/07/2025");
            transactions[1]["Date"].Should().Be("03/07/2025");
            // The bled-in "Money" is moved out of the Date column into the Description column.
            transactions[1]["Description"].Should().Contain("Money");

            // No transaction may carry a footer/print stamp in its Date column.
            transactions.Should().OnlyContain(t => DateColumnNormalizer.IsTransactionDate(t["Date"]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_RealWorldLayout_ShouldMergeMultiLineDescriptions_PerTransaction()
    {
        // Mirrors the real statement: a two-column "Booking Date" / "Description" table where each
        // transaction's description wraps across 2-4 physical lines that must merge into one cell.
        string[] columns = ["Booking Date", "Description"];
        const double dateX = 50;
        const double descX = 200;

        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.A4);

        AddText(page, font, dateX, 800, "Booking Date");
        AddText(page, font, descX, 800, "Description");

        // Transaction 1: date + 3 wrapped description lines.
        AddText(page, font, dateX, 760, "21 Aug 2025");
        AddText(page, font, descX, 760, "Money Transferred to MUHAMMAD");
        AddText(page, font, descX, 745, "AHMED MIRZA JazzCash");
        AddText(page, font, descX, 730, "XXXX0344222 STAN(956332)");

        // Transaction 2: date + 3 wrapped description lines.
        AddText(page, font, dateX, 700, "24 Aug 2025");
        AddText(page, font, descX, 700, "Money Received from");
        AddText(page, font, descX, 685, "ANWAR AA IAA HBL");
        AddText(page, font, descX, 670, "XXXX7901624403 STAN(309611)");

        // Transaction 3: date + 2 wrapped description lines.
        AddText(page, font, dateX, 640, "29 Aug 2025");
        AddText(page, font, descX, 640, "Charges Taxes Plus");
        AddText(page, font, descX, 625, "FED STAN(813409)");

        byte[] bytes = builder.Build();
        string path = Path.Combine(Path.GetTempPath(), $"statement_{Guid.NewGuid()}.pdf");
        File.WriteAllBytes(path, bytes);

        try
        {
            var parser = new PdfStatementParser(columns);

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            transactions.Should().HaveCount(3);

            transactions[0]["Booking Date"].Should().Be("21/08/2025");
            transactions[0]["Description"].Should()
                .Be("Money Transferred to MUHAMMAD AHMED MIRZA JazzCash XXXX0344222 STAN(956332)");

            transactions[1]["Booking Date"].Should().Be("24/08/2025");
            transactions[1]["Description"].Should()
                .Be("Money Received from ANWAR AA IAA HBL XXXX7901624403 STAN(309611)");

            transactions[2]["Booking Date"].Should().Be("29/08/2025");
            transactions[2]["Description"].Should().Be("Charges Taxes Plus FED STAN(813409)");

            transactions.Should().OnlyContain(t => DateColumnNormalizer.IsTransactionDate(t["Booking Date"]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateStatementPdf()
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.A4);

        // Header row.
        AddRow(page, font, 800, "Date", "Description", "Debit", "Credit", "Balance");

        // Data rows.
        AddRow(page, font, 770, "01/01/2025", "Opening balance", null, null, "1,000.00");
        AddRow(page, font, 745, "02/01/2025", "Card payment", "50.00", null, "950.00");
        // Continuation line (no date) belonging to the previous transaction.
        AddText(page, font, DescriptionX, 728, "Amazon UK");
        AddRow(page, font, 703, "03/01/2025", "Salary", null, "2,000.00", "2,950.00");

        byte[] bytes = builder.Build();
        string path = Path.Combine(Path.GetTempPath(), $"statement_{Guid.NewGuid()}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AddRow(
        PdfPageBuilder page,
        PdfDocumentBuilder.AddedFont font,
        double y,
        string date,
        string description,
        string? debit,
        string? credit,
        string balance)
    {
        AddText(page, font, DateX, y, date);
        AddText(page, font, DescriptionX, y, description);
        if (debit is not null)
        {
            AddText(page, font, DebitX, y, debit);
        }

        if (credit is not null)
        {
            AddText(page, font, CreditX, y, credit);
        }

        AddText(page, font, BalanceX, y, balance);
    }

    private static void AddText(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font, double x, double y, string text)
    {
        page.AddText(text, 10, new PdfPoint(x, y), font);
    }
}

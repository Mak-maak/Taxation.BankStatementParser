using FluentAssertions;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class PdfStatementParserAutoDetectTests
{
    [Fact]
    public void Parse_AutoDetect_ShouldDiscoverColumnsWithoutBeingTold()
    {
        string path = CreateStatementPdf(
            headerCells: ["Date", "Description", "Debit", "Credit", "Balance"],
            positions: [50, 130, 330, 410, 490]);

        try
        {
            var parser = new PdfStatementParser();

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            parser.Columns.Should().Equal("Transaction Date", "Description", "Debit", "Credit", "Balance");
            transactions.Should().HaveCount(3);
            transactions[0]["Transaction Date"].Should().Be("01/01/2025");
            transactions[0]["Description"].Should().Be("Opening balance");
            transactions[2]["Credit"].Should().Be("2,000.00");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_AutoDetect_ShouldDetectHeaderDecoratedWithOcrNoise()
    {
        // Scanned statements often carry decorated headings, e.g. "Date(DD/MM)" and
        // "***Particulars******". The header must still be recognised despite the surrounding
        // parentheses / asterisks that OCR keeps attached to the heading words.
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.A4);

        double[] positions = [50, 170, 290, 430];
        AddRow(page, font, 800, ["Date(DD/MM)", "Value", "*******Doc.No*****", "***Particulars******"], positions);
        AddRow(page, font, 770, ["02/07/25", "02/07/25", "", "Opening balance"], positions);
        AddRow(page, font, 745, ["03/07/25", "03/07/25", "07715275", "Cash Withdrawal"], positions);

        string path = WriteToTemp(builder);
        try
        {
            var parser = new PdfStatementParser();

            parser.Parse(path);

            parser.Columns.Should().Contain("Transaction Date");
            parser.Columns.Should().Contain("Description");
            parser.Columns[parser.AnchorColumnIndex].Should().Be("Transaction Date");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_AutoDetect_ShouldMapMultiWordHeadingsToCanonicalNames()
    {
        // "Money Out" / "Money In" are Debit / Credit synonyms and are normalized to the canonical
        // column names in the output.
        string path = CreateStatementPdf(
            headerCells: ["Transaction Date", "Description", "Money Out", "Money In", "Balance"],
            positions: [50, 170, 330, 410, 490]);

        try
        {
            var parser = new PdfStatementParser();

            parser.Parse(path);

            parser.Columns.Should().Equal("Transaction Date", "Description", "Debit", "Credit", "Balance");
            parser.Columns[parser.AnchorColumnIndex].Should().Be("Transaction Date");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_AutoDetect_WithTwoDateColumns_ShouldAnchorOnTransactionDate_AndIgnoreValueDate()
    {
        // A statement carrying both a Value Date (first) and a Transaction Date must anchor rows on
        // the Transaction Date, never the Value Date. The Value Date is not a canonical output column,
        // so it is ignored entirely and never appears in the parsed columns.
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.A4);

        double valueX = 50;
        double txnX = 160;
        double descX = 300;
        double amountX = 480;

        AddText(page, font, valueX, 800, "Value Date");
        AddText(page, font, txnX, 800, "Transaction Date");
        AddText(page, font, descX, 800, "Description");
        AddText(page, font, amountX, 800, "Debit");

        AddText(page, font, valueX, 770, "31/12/2024");
        AddText(page, font, txnX, 770, "01/01/2025");
        AddText(page, font, descX, 770, "Opening balance");
        AddText(page, font, amountX, 770, "1,000.00");

        AddText(page, font, valueX, 745, "01/01/2025");
        AddText(page, font, txnX, 745, "02/01/2025");
        AddText(page, font, descX, 745, "Card payment");
        AddText(page, font, amountX, 745, "50.00");

        string path = WriteToTemp(builder);
        try
        {
            var parser = new PdfStatementParser();

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            parser.Columns.Should().NotContain("Value Date");
            parser.Columns[parser.AnchorColumnIndex].Should().Be("Transaction Date");
            transactions.Should().HaveCount(2);
            transactions[0]["Transaction Date"].Should().Be("01/01/2025");
            transactions[1]["Transaction Date"].Should().Be("02/01/2025");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_AutoDetect_ShouldRestrictToCanonicalColumns_AndIgnoreOthers()
    {
        // The statement carries extra, non-canonical columns (Serial No, Branch). Only the canonical
        // set is kept: Transaction Date, Description, Ref/Cheque No, Debit, Credit, Balance.
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.A4);

        double serialX = 40;
        double dateX = 100;
        double descX = 220;
        double branchX = 360;
        double chequeX = 430;
        double debitX = 500;
        double balanceX = 560;

        AddText(page, font, serialX, 800, "Serial No");
        AddText(page, font, dateX, 800, "Transaction Date");
        AddText(page, font, descX, 800, "Description");
        AddText(page, font, branchX, 800, "Branch");
        AddText(page, font, chequeX, 800, "Cheque No");
        AddText(page, font, debitX, 800, "Debit");
        AddText(page, font, balanceX, 800, "Balance");

        AddText(page, font, serialX, 770, "1");
        AddText(page, font, dateX, 770, "01/01/2025");
        AddText(page, font, descX, 770, "Cheque payment");
        AddText(page, font, branchX, 770, "0123");
        AddText(page, font, chequeX, 770, "556677");
        AddText(page, font, debitX, 770, "250.00");
        AddText(page, font, balanceX, 770, "750.00");

        string path = WriteToTemp(builder);
        try
        {
            var parser = new PdfStatementParser();

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            parser.Columns.Should().Equal(
                "Transaction Date", "Description", "Ref/Cheque No", "Debit", "Balance");
            parser.Columns.Should().NotContain("Serial No");
            parser.Columns.Should().NotContain("Branch");

            transactions.Should().HaveCount(1);
            transactions[0]["Transaction Date"].Should().Be("01/01/2025");
            transactions[0]["Description"].Should().Be("Cheque payment");
            transactions[0]["Ref/Cheque No"].Should().Be("556677");
            transactions[0]["Debit"].Should().Be("250.00");
            transactions[0]["Balance"].Should().Be("750.00");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_AutoDetect_WithTightlyPackedSubHeadings_ShouldKeepOnlyCanonicalColumns()
    {
        // Reproduces a real statement whose header packs many sub-headings close together:
        // "Tran. Date  Effect Date  Tran. Br.  Transaction Details  Remitter Name  Remitter IBAN
        //  Remitter Bank  Chq / Ref No  Debit  Credit  Balance".
        // Only the six canonical columns must be produced; all other headings are ignored.
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(1400, 600);

        double tranDateX = 30;
        double effectDateX = 95;
        double tranBrX = 230;
        double detailsX = 320;
        double remitterNameX = 480;
        double remitterIbanX = 620;
        double remitterBankX = 760;
        double chqRefX = 900;
        double debitX = 1050;
        double creditX = 1170;
        double balanceX = 1290;

        AddText(page, font, tranDateX, 550, "Tran. Date");
        AddText(page, font, effectDateX, 550, "Effect Date");
        AddText(page, font, tranBrX, 550, "Tran. Br.");
        AddText(page, font, detailsX, 550, "Transaction Details");
        AddText(page, font, remitterNameX, 550, "Remitter Name");
        AddText(page, font, remitterIbanX, 550, "Remitter IBAN");
        AddText(page, font, remitterBankX, 550, "Remitter Bank");
        AddText(page, font, chqRefX, 550, "Chq / Ref No");
        AddText(page, font, debitX, 550, "Debit");
        AddText(page, font, creditX, 550, "Credit");
        AddText(page, font, balanceX, 550, "Balance");

        AddText(page, font, tranDateX, 520, "02/06/2025");
        AddText(page, font, detailsX, 520, "MONTHLY BUNDLE");
        AddText(page, font, chqRefX, 520, "0894");
        AddText(page, font, creditX, 520, "1.00");
        AddText(page, font, balanceX, 520, "599,999.86");

        string path = WriteToTemp(builder);
        try
        {
            var parser = new PdfStatementParser();

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            parser.Columns.Should().Equal(
                "Transaction Date", "Description", "Ref/Cheque No", "Debit", "Credit", "Balance");
            parser.Columns[parser.AnchorColumnIndex].Should().Be("Transaction Date");

            transactions.Should().HaveCount(1);
            transactions[0]["Transaction Date"].Should().Be("02/06/2025");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateStatementPdf(string[] headerCells, double[] positions)
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.A4);

        AddRow(page, font, 800, headerCells, positions);
        AddRow(page, font, 770, ["01/01/2025", "Opening balance", "", "", "1,000.00"], positions);
        AddRow(page, font, 745, ["02/01/2025", "Card payment", "50.00", "", "950.00"], positions);
        AddRow(page, font, 720, ["03/01/2025", "Salary", "", "2,000.00", "2,950.00"], positions);

        return WriteToTemp(builder);
    }

    private static string WriteToTemp(PdfDocumentBuilder builder)
    {
        byte[] bytes = builder.Build();
        string path = Path.Combine(Path.GetTempPath(), $"auto_statement_{Guid.NewGuid()}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AddRow(
        PdfPageBuilder page,
        PdfDocumentBuilder.AddedFont font,
        double y,
        string[] cells,
        double[] positions)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (!string.IsNullOrEmpty(cells[i]))
            {
                AddText(page, font, positions[i], y, cells[i]);
            }
        }
    }

    private static void AddText(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font, double x, double y, string text)
    {
        page.AddText(text, 10, new PdfPoint(x, y), font);
    }
}

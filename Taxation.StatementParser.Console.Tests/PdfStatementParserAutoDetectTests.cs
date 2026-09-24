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

    [Fact]
    public void Parse_AutoDetect_WithAsteriskDecoratedAmountHeaders_ShouldKeepDebitCreditBalanceSeparate()
    {
        // Reproduces a real statement whose amount headings are wrapped in decorative asterisks:
        // "Date(DD/MM)  Value  *******Doc.No*******  ***Particulars*****  *******Debit*******
        //  *******Credit*******  ******Balance*******".
        // The asterisks inflate each heading's width, shrinking the gap to its neighbour. Without
        // trimming them the three amount headings merge into one column (misclassified as Balance),
        // collapsing Debit/Credit/Balance together. They must remain three distinct columns.
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(1400, 600);

        double dateX = 30;
        double valueX = 150;
        double docX = 240;
        double particularsX = 410;
        double debitX = 640;
        double creditX = 760;
        double balanceX = 880;

        AddText(page, font, dateX, 550, "Date(DD/MM)");
        AddText(page, font, valueX, 550, "Value");
        AddText(page, font, docX, 550, "*******Doc.No*******");
        AddText(page, font, particularsX, 550, "***Particulars*****");
        AddText(page, font, debitX, 550, "*******Debit*******");
        AddText(page, font, creditX, 550, "*******Credit*******");
        AddText(page, font, balanceX, 550, "******Balance*******");

        AddText(page, font, dateX, 520, "02/07/25");
        AddText(page, font, valueX, 520, "02/07/25");
        AddText(page, font, particularsX, 520, "Ufone Super Card Max");
        AddText(page, font, debitX, 520, "-1,499.00");
        AddText(page, font, balanceX, 520, "7,315,907.26");
        // A "Transaction De" hyperlink sits to the right, geometrically overlapping the Balance
        // column. It must be stripped so the amount cell holds only the monetary value.
        AddText(page, font, 1080, 520, "Transaction");
        AddText(page, font, 1200, 520, "De");

        string path = WriteToTemp(builder);
        try
        {
            var parser = new PdfStatementParser();

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            parser.Columns.Should().Contain("Debit");
            parser.Columns.Should().Contain("Credit");
            parser.Columns.Should().Contain("Balance");
            parser.Columns[parser.AnchorColumnIndex].Should().Be("Transaction Date");

            transactions.Should().HaveCount(1);
            transactions[0]["Debit"].Should().Be("-1,499.00");
            transactions[0]["Balance"].Should().Be("7,315,907.26");
            transactions[0]["Credit"].Should().BeNullOrEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_AutoDetect_WithSummaryFirstPage_AndTwoDateColumns_ShouldParseTransactions()
    {
        // Reproduces a Meezan statement: a first page carrying an "Account Summary" / "Term Deposit
        // Summary" (no Date column) that must be ignored, followed by a transaction page whose header
        // is "Date  Value Date  Doc No  Particular  Debit  Credit  Balance". The anchor is the
        // Transaction Date (not the Value Date); rows use the dd-MMM-yyyy date style and a "-"
        // placeholder in empty amount cells.
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // Page 1: summary tables (no Date column) - must be skipped entirely.
        PdfPageBuilder summary = builder.AddPage(820, 600);
        AddText(summary, font, 40, 550, "Product");
        AddText(summary, font, 200, 550, "Account Number");
        AddText(summary, font, 340, 550, "IBAN");
        AddText(summary, font, 520, 550, "Currency");
        AddText(summary, font, 620, 550, "FCY Balance");
        AddText(summary, font, 740, 550, "Balance");
        AddText(summary, font, 40, 520, "Meezan Rupee Current A/c");
        AddText(summary, font, 200, 520, "0105327871");
        AddText(summary, font, 740, 520, "2,000.43");

        // Page 2: the transaction table.
        PdfPageBuilder page = builder.AddPage(820, 700);
        double dateX = 20;
        double valueDateX = 95;
        double docX = 175;
        double particularX = 265;
        double debitX = 590;
        double creditX = 670;
        double balanceX = 745;

        AddText(page, font, dateX, 660, "Date");
        AddText(page, font, valueDateX, 660, "Value Date");
        AddText(page, font, docX, 660, "Doc No");
        AddText(page, font, particularX, 660, "Particular");
        AddText(page, font, debitX, 660, "Debit");
        AddText(page, font, creditX, 660, "Credit");
        AddText(page, font, balanceX, 660, "Balance");

        // Opening balance line (has a date, no Value Date/amounts except balance).
        AddText(page, font, dateX, 630, "01-Jan-2026");
        AddText(page, font, particularX, 630, "<=Opening Balance=>");
        AddText(page, font, balanceX, 630, "3,000.43");

        AddText(page, font, dateX, 600, "02-Jan-2026");
        AddText(page, font, valueDateX, 600, "02-Jan-2026");
        AddText(page, font, particularX, 600, ".... STAN(897247) BY MEEZAN RAAST");
        AddText(page, font, debitX, 600, "-");
        AddText(page, font, creditX, 600, "27,000.00");
        AddText(page, font, balanceX, 600, "30,000.43");

        AddText(page, font, dateX, 570, "02-Jan-2026");
        AddText(page, font, valueDateX, 570, "02-Jan-2026");
        AddText(page, font, particularX, 570, ".... STAN(879717) PK49SADA");
        AddText(page, font, debitX, 570, "790.00");
        AddText(page, font, creditX, 570, "-");
        AddText(page, font, balanceX, 570, "29,210.43");

        string path = WriteToTemp(builder);
        try
        {
            var parser = new PdfStatementParser();

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            parser.Columns.Should().Contain("Transaction Date");
            parser.Columns.Should().Contain("Description");
            parser.Columns.Should().Contain("Debit");
            parser.Columns.Should().Contain("Credit");
            parser.Columns.Should().Contain("Balance");
            parser.Columns[parser.AnchorColumnIndex].Should().Be("Transaction Date");

            transactions.Should().HaveCount(3);
            transactions[1]["Transaction Date"].Should().Be("02/01/2026");
            transactions[1]["Credit"].Should().Be("27,000.00");
            transactions[1]["Debit"].Should().BeNullOrEmpty();
            transactions[1]["Description"].Should().Contain("MEEZAN");
            transactions[2]["Debit"].Should().Be("790.00");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_AutoDetect_WithCompactDatesAndWithdrawalDeposit_ShouldParseTransactions()
    {
        // Reproduces a Standard Chartered statement: columns "Date  Description  Withdrawal  Deposit
        // Balance" with compact separator-less dates such as "01Jun25". The compact date must anchor
        // each transaction row, otherwise the parser reports "No transactions were detected".
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(760, 620);

        double dateX = 20;
        double descX = 90;
        double withdrawalX = 400;
        double depositX = 520;
        double balanceX = 640;

        AddText(page, font, dateX, 590, "Date");
        AddText(page, font, descX, 590, "Description");
        AddText(page, font, withdrawalX, 590, "Withdrawal");
        AddText(page, font, depositX, 590, "Deposit");
        AddText(page, font, balanceX, 590, "Balance");

        AddText(page, font, dateX, 560, "01Jun25");
        AddText(page, font, descX, 560, "BALANCE B/F");
        AddText(page, font, balanceX, 560, "604,993.64");

        AddText(page, font, dateX, 530, "02Jun25");
        AddText(page, font, descX, 530, "IBANKING TRF FROM 01982518 V.010625");
        AddText(page, font, withdrawalX, 530, "01982518");
        AddText(page, font, depositX, 530, "40,000.00");
        AddText(page, font, balanceX, 530, "644,993.64");

        AddText(page, font, dateX, 500, "02Jun25");
        AddText(page, font, descX, 500, "ATM WDR AT 960129 04:05:35 V.010625");
        AddText(page, font, withdrawalX, 500, "20,000.00");
        AddText(page, font, balanceX, 500, "624,993.64");

        AddText(page, font, dateX, 470, "10Jun25");
        AddText(page, font, descX, 470, "DC TXN PKR 2500.00 ON 03/JUN");
        AddText(page, font, withdrawalX, 470, "2,500.00");
        AddText(page, font, balanceX, 470, "480,243.64");

        string path = WriteToTemp(builder);
        try
        {
            var parser = new PdfStatementParser();

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            parser.Columns.Should().Contain("Transaction Date");
            parser.Columns.Should().Contain("Debit");
            parser.Columns.Should().Contain("Credit");
            parser.Columns.Should().Contain("Balance");

            transactions.Should().HaveCount(3);
            // The leading "BALANCE B/F" carry-forward row is ignored (not a real transaction).
            transactions[0]["Transaction Date"].Should().Be("02/06/2025");
            transactions[0]["Credit"].Should().Be("40,000.00");
            // A reference number that geometrically overlaps the Withdrawal column must be rejected:
            // amount columns only accept genuine monetary values, never identifiers like "01982518".
            transactions[0]["Debit"].Should().BeEmpty();
            transactions[1]["Debit"].Should().Be("20,000.00");
            transactions[2]["Transaction Date"].Should().Be("10/06/2025");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_AutoDetect_WithRepeatingPageBanner_ShouldNotCorruptLastTransaction()
    {
        // Reproduces a multi-page Standard Chartered statement where every page repeats an account
        // banner (From/To Date, Statement No, Account No, masked account) ABOVE the table header.
        // The last transaction on page 1 must NOT absorb page 2's banner as a continuation line.
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);

        double dateX = 20;
        double descX = 90;
        double withdrawalX = 400;
        double depositX = 520;
        double balanceX = 640;

        // Page 1: banner, header, two transactions.
        PdfPageBuilder page1 = builder.AddPage(760, 620);
        AddText(page1, font, descX, 600, "From Date: 01/06/2025  To Date: 30/06/2025  Account No: *******8401");
        AddText(page1, font, dateX, 560, "Date");
        AddText(page1, font, descX, 560, "Description");
        AddText(page1, font, withdrawalX, 560, "Withdrawal");
        AddText(page1, font, depositX, 560, "Deposit");
        AddText(page1, font, balanceX, 560, "Balance");
        AddText(page1, font, dateX, 530, "05Jun25");
        AddText(page1, font, descX, 530, "IBANKING TRF TO 8301");
        AddText(page1, font, withdrawalX, 530, "20,000.00");
        AddText(page1, font, balanceX, 530, "506,743.64");
        AddText(page1, font, dateX, 500, "10Jun25");
        AddText(page1, font, descX, 500, "DC TXN PKR 2500.00 ON 03/JUN JAVAID SERVICE STATION");
        AddText(page1, font, withdrawalX, 500, "2,500.00");
        AddText(page1, font, balanceX, 500, "480,243.64");

        // Page 2: repeating banner (with masked numbers/dates), header, one transaction.
        PdfPageBuilder page2 = builder.AddPage(760, 620);
        AddText(page2, font, descX, 600, "From Date: 01/06/2025  To Date: 31/07/2026  Account No: 00922132489025 *******8401");
        AddText(page2, font, dateX, 560, "Date");
        AddText(page2, font, descX, 560, "Description");
        AddText(page2, font, withdrawalX, 560, "Withdrawal");
        AddText(page2, font, depositX, 560, "Deposit");
        AddText(page2, font, balanceX, 560, "Balance");
        // Dateless carry-forward marker repeated at the top of the continuation page.
        AddText(page2, font, descX, 545, "BALANCE B/F");
        AddText(page2, font, balanceX, 545, "480,243.64");
        AddText(page2, font, dateX, 530, "10Jun25");
        AddText(page2, font, descX, 530, "PK-019-250608-203132692-27 V.080625");
        AddText(page2, font, withdrawalX, 530, "50,000.00");
        AddText(page2, font, balanceX, 530, "430,243.64");

        string path = WriteToTemp(builder);
        try
        {
            var parser = new PdfStatementParser();

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            transactions.Should().HaveCount(3);

            // The last transaction on page 1 must remain clean: no banner dates/numbers merged in.
            StatementTransaction lastOnPage1 = transactions[1];
            lastOnPage1["Transaction Date"].Should().Be("10/06/2025");
            lastOnPage1["Description"].Should().NotContain("00922132489025");
            lastOnPage1["Description"].Should().NotContain("From Date");
            lastOnPage1["Credit"].Should().BeEmpty();
            lastOnPage1["Balance"].Should().Be("480,243.64");

            // Page 2's transaction is parsed independently and correctly.
            transactions[2]["Transaction Date"].Should().Be("10/06/2025");
            transactions[2]["Debit"].Should().Be("50,000.00");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_AutoDetect_WhenReferenceNumberDriftsIntoAmountColumn_ShouldKeepOnlyTheRealAmount()
    {
        // Reproduces a Meezan statement corruption: a STAN reference number such as "826971" drifts
        // geometrically under the Debit column next to the real amount "7,500.00". The parser must
        // reject the bare-integer reference so the Debit is "7,500.00" and NOT "8,269,717,500.00".
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(760, 620);

        double dateX = 20;
        double descX = 90;
        double debitX = 430;
        double creditX = 540;
        double balanceX = 650;

        AddText(page, font, dateX, 590, "Date");
        AddText(page, font, descX, 590, "Description");
        AddText(page, font, debitX, 590, "Debit");
        AddText(page, font, creditX, 590, "Credit");
        AddText(page, font, balanceX, 590, "Balance");

        // The reference "826971" appears in the description flow; the real amount is under Debit.
        AddText(page, font, dateX, 560, "19/01/2026");
        AddText(page, font, descX, 560, "MBANKING FUNDS TRANSFER STAN (826971) TO:VENTURE GAMES");
        AddText(page, font, debitX, 560, "7,500.00");
        AddText(page, font, balanceX, 560, "21,516.43");

        string path = WriteToTemp(builder);
        try
        {
            var parser = new PdfStatementParser();

            IReadOnlyList<StatementTransaction> transactions = parser.Parse(path);

            transactions.Should().HaveCount(1);
            transactions[0]["Debit"].Should().Be("7,500.00");
            transactions[0]["Description"].Should().Contain("MBANKING FUNDS TRANSFER STAN");
            transactions[0]["Balance"].Should().Be("21,516.43");
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

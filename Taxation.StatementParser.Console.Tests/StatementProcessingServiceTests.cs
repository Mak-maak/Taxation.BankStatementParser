using FluentAssertions;
using Taxation.StatementParser.Console.Processing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class StatementProcessingServiceTests
{
    private static readonly string[] Columns = ["Date", "Description", "Debit", "Credit", "Balance"];

    private const double DateX = 50;
    private const double DescriptionX = 130;
    private const double DebitX = 330;
    private const double CreditX = 410;
    private const double BalanceX = 490;

    [Fact]
    public void Process_WithFewerThanTwoColumns_ShouldFail()
    {
        StatementProcessingResult result = new StatementProcessingService().Process(["Date"], "any.pdf");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("two");
    }

    [Fact]
    public void Process_WithDuplicateColumns_ShouldFail()
    {
        StatementProcessingResult result = new StatementProcessingService()
            .Process(["Date", "date"], "any.pdf");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Duplicate");
    }

    [Fact]
    public void Process_WithBlankPath_ShouldFail()
    {
        StatementProcessingResult result = new StatementProcessingService()
            .Process(Columns, "   ");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("required");
    }

    [Fact]
    public void Process_WithMissingFile_ShouldFail()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");

        StatementProcessingResult result = new StatementProcessingService().Process(Columns, missing);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public void Process_WithNonPdfFile_ShouldFail()
    {
        string txt = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");
        File.WriteAllText(txt, "not a pdf");
        try
        {
            StatementProcessingResult result = new StatementProcessingService().Process(Columns, txt);

            result.Ok.Should().BeFalse();
            result.Error.Should().Contain("not supported");
        }
        finally
        {
            File.Delete(txt);
        }
    }

    [Fact]
    public void Process_WhenNoTransactionsDetected_ShouldFail()
    {
        string path = CreateStatementPdf();
        try
        {
            // Column names that do not match any header so no transactions are produced,
            // but the parser throws which the service converts into a failure result.
            StatementProcessingResult result = new StatementProcessingService()
                .Process(["Alpha", "Beta", "Gamma"], path);

            result.Ok.Should().BeFalse();
            result.Error.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Process_WithValidStatement_ShouldWriteWorkbookAndSucceed()
    {
        string path = CreateStatementPdf();
        var logged = new List<string>();
        try
        {
            StatementProcessingResult result = new StatementProcessingService()
                .Process(Columns, path, logged.Add);

            result.Ok.Should().BeTrue();
            result.Error.Should().BeNull();
            result.TransactionCount.Should().BeGreaterThan(0);
            result.GroupCount.Should().BeGreaterThan(0);
            result.Columns.Should().Equal(Columns);
            result.ColumnTypes.Should().HaveCount(Columns.Length);

            result.OutputPath.Should().NotBeNullOrWhiteSpace();
            File.Exists(result.OutputPath!).Should().BeTrue();
            Path.GetExtension(result.OutputPath).Should().Be(".xlsx");

            // The generated workbook has a friendly, readable name.
            string outputName = Path.GetFileName(result.OutputPath!);
            outputName.Should().Contain("Parsed Statement");
            outputName.Should().StartWith(Path.GetFileNameWithoutExtension(path));
        }
        finally
        {
            File.Delete(path);
            // Best-effort cleanup of the produced workbook (path is inside temp).
            foreach (string leftover in Directory.EnumerateFiles(
                         Path.GetDirectoryName(path)!, "*Parsed Statement*.xlsx"))
            {
                try { File.Delete(leftover); } catch { /* ignore */ }
            }
        }
    }

    [Theory]
    [InlineData("\"C:\\Temp\\file.pdf\"", "C:\\Temp\\file.pdf")]
    [InlineData("'C:\\Temp\\file.pdf'", "C:\\Temp\\file.pdf")]
    [InlineData("  C:\\Temp\\file.pdf  ", "C:\\Temp\\file.pdf")]
    public void NormalizePath_ShouldTrimQuotesAndWhitespace(string input, string expectedEnding)
    {
        StatementProcessingService.NormalizePath(input).Should().EndWith(expectedEnding);
    }

    [Fact]
    public void NormalizePath_WithEmptyInput_ShouldReturnEmpty()
    {
        StatementProcessingService.NormalizePath("   ").Should().BeEmpty();
    }

    [Fact]
    public void NormalizePath_ShouldExpandEnvironmentVariables()
    {
        string result = StatementProcessingService.NormalizePath("%TEMP%\\statement.pdf");

        result.Should().NotContain("%TEMP%");
        result.Should().EndWith("statement.pdf");
    }

    private static string CreateStatementPdf()
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.A4);

        AddRow(page, font, 800, "Date", "Description", "Debit", "Credit", "Balance");
        AddRow(page, font, 770, "01/01/2025", "Opening balance", null, null, "1,000.00");
        AddRow(page, font, 745, "02/01/2025", "Card payment", "50.00", null, "950.00");
        AddRow(page, font, 720, "03/01/2025", "Salary", null, "2,000.00", "2,950.00");

        byte[] bytes = builder.Build();
        string path = Path.Combine(Path.GetTempPath(), $"svc_statement_{Guid.NewGuid()}.pdf");
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

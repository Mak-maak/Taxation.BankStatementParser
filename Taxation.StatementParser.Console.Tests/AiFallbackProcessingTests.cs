using FluentAssertions;
using Taxation.StatementParser.Console.Configuration;
using Taxation.StatementParser.Console.Parsing.Ai;
using Taxation.StatementParser.Console.Processing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class AiFallbackProcessingTests
{
    private const double DateX = 50;
    private const double DescriptionX = 130;
    private const double DebitX = 330;
    private const double CreditX = 410;
    private const double BalanceX = 490;

    /// <summary>Test double for the local AI extractor; records calls and returns canned results.</summary>
    private sealed class FakeAiExtractor : IAiStatementExtractor
    {
        public bool Available { get; init; } = true;
        public AiExtractionResult Result { get; init; } = AiExtractionResult.Failure("not configured");
        public int ExtractCalls { get; private set; }
        public int AvailabilityCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            AvailabilityCalls++;
            return Task.FromResult(Available);
        }

        public Task<AiExtractionResult> ExtractAsync(
            string documentPath,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            ExtractCalls++;
            return Task.FromResult(Result);
        }
    }

    private static AppSettings SettingsWithAi(string enabled, string mode = "fallback") =>
        new() { Ai = new AiSettings { Enabled = enabled, Mode = mode } };

    private static AiExtractionResult ReconcilingResult() =>
        AiExtractionResult.Success(
        [
            new AiExtractionRow("01/01/2025", "Opening balance", "", "", "1,000.00"),
            new AiExtractionRow("02/01/2025", "Card payment", "50.00", "", "950.00"),
            new AiExtractionRow("03/01/2025", "Salary", "", "2,000.00", "2,950.00"),
        ]);

    [Fact]
    public void Process_WhenAiDisabled_ShouldNeverCallExtractor()
    {
        string path = CreateStatementPdf();
        var fake = new FakeAiExtractor { Result = ReconcilingResult() };
        try
        {
            var service = new StatementProcessingService(
                SettingsWithAi("no"),
                (_, _) => fake);

            StatementProcessingResult result = service.Process(path);

            result.Ok.Should().BeTrue();
            fake.ExtractCalls.Should().Be(0);
            fake.AvailabilityCalls.Should().Be(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Process_WhenAlwaysModeAndAvailable_ShouldUseAiTransactions()
    {
        string path = CreateStatementPdf();
        var fake = new FakeAiExtractor { Available = true, Result = ReconcilingResult() };
        try
        {
            var logs = new List<string>();
            var service = new StatementProcessingService(
                SettingsWithAi("yes", "always"),
                (_, _) => fake);

            StatementProcessingResult result = service.Process(path, logs.Add);

            result.Ok.Should().BeTrue();
            fake.ExtractCalls.Should().Be(1);
            result.TransactionCount.Should().BeGreaterThan(0);
            logs.Should().Contain(l => l.Contains("AI extraction accepted"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Process_WhenAiUnavailable_ShouldKeepGeometricResult()
    {
        string path = CreateStatementPdf();
        var fake = new FakeAiExtractor { Available = false, Result = ReconcilingResult() };
        try
        {
            var logs = new List<string>();
            var service = new StatementProcessingService(
                SettingsWithAi("yes", "always"),
                (_, _) => fake);

            StatementProcessingResult result = service.Process(path, logs.Add);

            result.Ok.Should().BeTrue();
            fake.AvailabilityCalls.Should().Be(1);
            fake.ExtractCalls.Should().Be(0, "extraction must be skipped when the runtime is unavailable");
            logs.Should().NotContain(l => l.Contains("AI extraction accepted"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Process_WhenAiOutputDoesNotReconcile_ShouldRejectAndKeepGeometric()
    {
        string path = CreateStatementPdf();
        var garbled = AiExtractionResult.Success(
        [
            new AiExtractionRow("01/01/2025", "Opening", "", "", "1,000.00"),
            new AiExtractionRow("02/01/2025", "Junk", "50.00", "", "12.34"),
            new AiExtractionRow("03/01/2025", "Junk", "", "100.00", "9.99"),
        ]);
        var fake = new FakeAiExtractor { Available = true, Result = garbled };
        try
        {
            var logs = new List<string>();
            var service = new StatementProcessingService(
                SettingsWithAi("yes", "always"),
                (_, _) => fake);

            StatementProcessingResult result = service.Process(path, logs.Add);

            result.Ok.Should().BeTrue();
            fake.ExtractCalls.Should().Be(1);
            // Invalid AI output is rejected; the geometric result is kept.
            logs.Should().NotContain(l => l.Contains("AI extraction accepted"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Process_WhenExtractorThrows_ShouldNotFailAndKeepGeometric()
    {
        string path = CreateStatementPdf();
        try
        {
            var logs = new List<string>();
            var service = new StatementProcessingService(
                SettingsWithAi("yes", "always"),
                (_, _) => throw new InvalidOperationException("boom"));

            StatementProcessingResult result = service.Process(path, logs.Add);

            result.Ok.Should().BeTrue("AI errors must never break geometric processing");
            logs.Should().NotContain(l => l.Contains("AI extraction accepted"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Process_WhenGeometricFailsAndAiEnabled_ShouldRecoverViaAi()
    {
        // A document with no recognisable statement header/columns: the geometric engine cannot
        // detect the layout (the "new/slightly different format" scenario). AI should recover it.
        string path = CreateUnrecognizableLayoutPdf();
        var fake = new FakeAiExtractor { Available = true, Result = ReconcilingResult() };
        try
        {
            var logs = new List<string>();
            var service = new StatementProcessingService(
                SettingsWithAi("yes"),   // fallback mode
                (_, _) => fake);

            StatementProcessingResult result = service.Process(path, logs.Add);

            result.Ok.Should().BeTrue("AI must recover an unrecognised layout when enabled");
            fake.ExtractCalls.Should().Be(1);
            result.TransactionCount.Should().BeGreaterThan(0);
            logs.Should().Contain(l => l.Contains("AI extraction accepted"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Process_WhenGeometricFailsAndAiDisabled_ShouldFailWithEnableAiHint()
    {
        string path = CreateUnrecognizableLayoutPdf();
        try
        {
            var service = new StatementProcessingService(SettingsWithAi("no"));

            StatementProcessingResult result = service.Process(path);

            result.Ok.Should().BeFalse();
            result.Error.Should().NotBeNullOrWhiteSpace();
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

        AddRow(page, font, 800, "Date", "Description", "Debit", "Credit", "Balance");
        AddRow(page, font, 770, "01/01/2025", "Opening balance", null, null, "1,000.00");
        AddRow(page, font, 745, "02/01/2025", "Card payment", "50.00", null, "950.00");
        AddRow(page, font, 720, "03/01/2025", "Salary", null, "2,000.00", "2,950.00");

        byte[] bytes = builder.Build();
        string path = Path.Combine(Path.GetTempPath(), $"ai_svc_statement_{Guid.NewGuid()}.pdf");
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

    /// <summary>
    /// Builds a PDF with only free-form prose and no statement header/columns, so the geometric
    /// engine cannot detect a table (simulates a new/unrecognised layout that should trigger AI).
    /// </summary>
    private static string CreateUnrecognizableLayoutPdf()
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.A4);

        AddText(page, font, 60, 760, "This document contains no recognisable statement table.");
        AddText(page, font, 60, 730, "Lorem ipsum dolor sit amet consectetur adipiscing elit.");
        AddText(page, font, 60, 700, "There is no date column and no header row of any kind.");

        byte[] bytes = builder.Build();
        string path = Path.Combine(Path.GetTempPath(), $"ai_svc_unknown_{Guid.NewGuid()}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

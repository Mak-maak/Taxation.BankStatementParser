using FluentAssertions;
using Taxation.StatementParser.Console.Parsing.Ocr;
using Taxation.StatementParser.Console.Processing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class OcrSupportTests
{
    [Theory]
    [InlineData("statement.png", true)]
    [InlineData("statement.JPG", true)]
    [InlineData("statement.jpeg", true)]
    [InlineData("statement.tif", true)]
    [InlineData("statement.tiff", true)]
    [InlineData("statement.bmp", true)]
    [InlineData("statement.pdf", false)]
    [InlineData("statement.txt", false)]
    public void IsImagePath_ShouldRecogniseScannedImageFormats(string fileName, bool expected)
    {
        StatementProcessingService.IsImagePath(fileName).Should().Be(expected);
    }

    [Fact]
    public void SupportedExtensions_ShouldIncludePdfAndCommonImageFormats()
    {
        StatementProcessingService.SupportedExtensions
            .Should().Contain([".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"]);
    }

    [Fact]
    public void Process_WithUnsupportedExtension_ShouldFail()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "not a statement");

        try
        {
            StatementProcessingResult result = new StatementProcessingService().Process(path);

            result.Ok.Should().BeFalse();
            result.Error.Should().Contain("not supported");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Process_WithMissingImageFile_ShouldReportFileNotFound()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");

        StatementProcessingResult result = new StatementProcessingService().Process(path);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("File not found");
    }

    [Fact]
    public void OcrExtractor_WithUndecodableImageBytes_ShouldThrow()
    {
        using var extractor = new PaddleOcrTextExtractor();

        Action act = () => extractor.ExtractFromImageBytes([0x00, 0x01, 0x02, 0x03]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*could not be decoded*");
    }

    [Fact]
    public void OcrExtractor_WithMissingImageFile_ShouldThrowFileNotFound()
    {
        using var extractor = new PaddleOcrTextExtractor();

        Action act = () => extractor.ExtractFromImageFile(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png"));

        act.Should().Throw<FileNotFoundException>();
    }
}

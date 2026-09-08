using FluentAssertions;
using Taxation.StatementParser.Console.Web;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class WebUiPageTests
{
    [Fact]
    public void Html_ShouldLoadUiFromAssetsFolder()
    {
        // The asset is copied next to the test assembly on build.
        string html = WebUiPage.Html;

        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("<!DOCTYPE html>");
        html.Should().Contain("Bank Statement Parser");
        html.Should().Contain("Choose File");
        html.Should().Contain("/api/process");
    }

    [Fact]
    public void Html_WhenAssetMissing_ShouldReturnDiagnosticPage()
    {
        string assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "index.html");
        string backupPath = assetPath + ".bak";

        File.Exists(assetPath).Should().BeTrue("the UI asset should be deployed next to the app");

        File.Move(assetPath, backupPath, overwrite: true);
        try
        {
            string html = WebUiPage.Html;

            html.Should().Contain("UI asset not found");
            html.Should().Contain("index.html");
        }
        finally
        {
            File.Move(backupPath, assetPath, overwrite: true);
        }
    }
}

namespace Taxation.StatementParser.Console.Web;

/// <summary>
/// Loads the single-page HTML UI served by <see cref="StatementWebServer"/> from
/// <c>Assets/index.html</c> next to the application. Keeping the markup in a plain HTML file (copied
/// to the output folder on build) means colours and other tweaks can be made by editing that file
/// directly, without recompiling the application.
/// </summary>
internal static class WebUiPage
{
    private const string AssetsFolder = "Assets";
    private const string PageFileName = "index.html";

    /// <summary>
    /// Reads and returns the UI HTML from the assets folder. The file is read on each request so
    /// edits are picked up immediately (no restart needed).
    /// </summary>
    public static string Html
    {
        get
        {
            string path = ResolvePath();
            return File.Exists(path)
                ? File.ReadAllText(path)
                : BuildMissingAssetPage(path);
        }
    }

    private static string ResolvePath() =>
        Path.Combine(AppContext.BaseDirectory, AssetsFolder, PageFileName);

    private static string BuildMissingAssetPage(string expectedPath) =>
        $"""
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="utf-8" /><title>UI file missing</title></head>
        <body style="font-family: 'Segoe UI', sans-serif; padding: 32px; color: #b91c1c;">
            <h1>UI asset not found</h1>
            <p>The interface file could not be located. Expected it at:</p>
            <p><code>{System.Net.WebUtility.HtmlEncode(expectedPath)}</code></p>
        </body>
        </html>
        """;
}

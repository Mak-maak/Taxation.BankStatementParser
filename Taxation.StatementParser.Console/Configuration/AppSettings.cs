using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taxation.StatementParser.Console.Configuration;

/// <summary>
/// Strongly-typed application settings loaded from <c>appsettings.json</c> that sits next to the
/// executable. Kept intentionally lightweight (System.Text.Json only) so the tool has no extra
/// configuration dependencies.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Excel output protection settings.</summary>
    public ExcelProtectionSettings ExcelProtection { get; init; } = new();

    /// <summary>
    /// Loads settings from the <c>appsettings.json</c> file located alongside the running executable.
    /// Missing file, missing section or unreadable content all fall back to safe defaults
    /// (password protection ENABLED using the default password) so the workbook is never left
    /// unintentionally unprotected because of a configuration mistake.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            return JsonSerializer.Deserialize<AppSettings>(json, options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Never fail statement processing because of a configuration issue; fall back to defaults.
            return new AppSettings();
        }
    }
}

/// <summary>Controls whether (and with which password) generated workbooks are encrypted.</summary>
public sealed class ExcelProtectionSettings
{
    /// <summary>
    /// Whether the generated Excel workbook is password protected. Accepts friendly boolean values
    /// (<c>yes</c>/<c>no</c>, <c>true</c>/<c>false</c>, <c>1</c>/<c>0</c>, <c>on</c>/<c>off</c>).
    /// Defaults to <c>yes</c>.
    /// </summary>
    [JsonPropertyName("PasswordProtect")]
    public string PasswordProtect { get; init; } = "yes";

    /// <summary>Password used to open the workbook when protection is enabled.</summary>
    [JsonPropertyName("Password")]
    public string Password { get; init; } = "confidential_123";

    /// <summary>True when <see cref="PasswordProtect"/> represents an affirmative value.</summary>
    public bool IsProtectionEnabled =>
        PasswordProtect?.Trim().ToLowerInvariant() is "yes" or "true" or "1" or "on" or "y";
}

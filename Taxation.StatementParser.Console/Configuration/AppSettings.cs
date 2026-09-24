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

    /// <summary>Local, offline AI-assisted extraction settings (Ollama vision model).</summary>
    public AiSettings Ai { get; init; } = new();

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

/// <summary>
/// Settings for the optional, fully-offline AI extraction fallback. The tool talks to a local
/// Ollama vision model over the loopback interface only; no statement data ever leaves the machine.
/// Disabled by default so existing behaviour is unchanged until explicitly enabled.
/// </summary>
public sealed class AiSettings
{
    /// <summary>
    /// Whether AI-assisted extraction is enabled. Accepts friendly boolean values
    /// (<c>yes</c>/<c>no</c>, <c>true</c>/<c>false</c>, <c>1</c>/<c>0</c>, <c>on</c>/<c>off</c>).
    /// Defaults to <c>no</c> (off) so nothing changes until the operator opts in.
    /// </summary>
    [JsonPropertyName("Enabled")]
    public string Enabled { get; init; } = "no";

    /// <summary>
    /// Base address of the local Ollama service. MUST be a loopback address; any non-loopback host
    /// is rejected at runtime so confidential data can never be sent to a remote server.
    /// </summary>
    [JsonPropertyName("Endpoint")]
    public string Endpoint { get; init; } = "http://localhost:11434";

    /// <summary>Local multimodal (vision) model used for both scanned images and rasterized PDFs.</summary>
    [JsonPropertyName("Model")]
    public string Model { get; init; } = "llama3.2-vision";

    /// <summary>
    /// When <c>always</c>, AI runs for every statement. When <c>fallback</c> (default), AI runs only
    /// for scanned/image inputs or when the geometric parser produces no/invalid transactions.
    /// </summary>
    [JsonPropertyName("Mode")]
    public string Mode { get; init; } = "fallback";

    /// <summary>Per-request timeout (seconds) for the local model. Default 180.</summary>
    [JsonPropertyName("TimeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 180;

    /// <summary>Sampling temperature. Default 0 for deterministic, repeatable extraction.</summary>
    [JsonPropertyName("Temperature")]
    public double Temperature { get; init; } = 0;

    /// <summary>Safety cap on the number of pages sent to the model per document. Default 40.</summary>
    [JsonPropertyName("MaxPages")]
    public int MaxPages { get; init; } = 40;

    /// <summary>True when <see cref="Enabled"/> represents an affirmative value.</summary>
    public bool IsEnabled =>
        Enabled?.Trim().ToLowerInvariant() is "yes" or "true" or "1" or "on" or "y";

    /// <summary>True when AI should always run (not just as a fallback).</summary>
    public bool IsAlwaysMode =>
        Mode?.Trim().ToLowerInvariant() is "always";

    /// <summary>
    /// True only when <see cref="Endpoint"/> resolves to a loopback address (localhost/127.0.0.1/::1).
    /// This is the hard privacy guarantee: extraction is refused for any non-loopback endpoint.
    /// </summary>
    public bool IsLoopbackEndpoint()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return true;
        }

        string host = uri.Host.Trim().ToLowerInvariant();
        return host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Taxation.StatementParser.Console.Configuration;
using Taxation.StatementParser.Console.Parsing.Ocr;
using UglyToad.PdfPig;

namespace Taxation.StatementParser.Console.Parsing.Ai;

/// <summary>
/// A fully-offline AI extractor backed by a local <c>Ollama</c> vision model (default
/// <c>llama3.2-vision</c>) reachable only over the loopback interface. Each statement page is
/// rasterized to PNG locally and sent to <c>http://localhost:11434</c>; no data leaves the machine.
/// <para>
/// The extractor is defensive by design: unreachable service, missing model, timeouts and malformed
/// model output all resolve to an unsuccessful result rather than an exception, so the processing
/// pipeline can fall back to the geometric parser without disruption.
/// </para>
/// </summary>
public sealed class OllamaVisionExtractor : IAiStatementExtractor, IDisposable
{
    private readonly AiSettings _settings;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly bool _ownsHttpClient;

    private const string ExtractionPrompt =
        "You are a precise bank-statement table extractor. Look at the image of a bank statement " +
        "page and extract EVERY transaction row from the table. Return ONLY a compact JSON array; " +
        "no prose, no markdown. Each element must be an object with EXACTLY these string keys: " +
        "\"date\", \"description\", \"debit\", \"credit\", \"balance\". Rules: copy amounts EXACTLY " +
        "as printed (keep commas and decimals); NEVER invent or guess digits; use an empty string " +
        "\"\" for any cell that is blank or absent; do not include header rows, opening/closing " +
        "balance carry-forward markers, page banners, or totals; keep rows in the order they appear.";

    /// <summary>Creates an extractor using the given settings and an internally-owned HttpClient.</summary>
    public OllamaVisionExtractor(AiSettings settings, Action<string>? log = null)
        : this(settings, CreateDefaultClient(settings), log, ownsHttpClient: true)
    {
    }

    /// <summary>Creates an extractor with an injected HttpClient (used by tests with a mock handler).</summary>
    public OllamaVisionExtractor(
        AiSettings settings,
        HttpClient httpClient,
        Action<string>? log = null,
        bool ownsHttpClient = false)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log ?? (_ => { });
        _ownsHttpClient = ownsHttpClient;
    }

    private static HttpClient CreateDefaultClient(AiSettings settings)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(settings.Endpoint),
            Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)),
        };

        return client;
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        // Hard privacy guarantee: never talk to a non-loopback endpoint.
        if (!_settings.IsLoopbackEndpoint())
        {
            _log($"AI disabled: endpoint '{_settings.Endpoint}' is not a loopback address.");
            return false;
        }

        try
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(probe.Token, cancellationToken);

            HttpResponseMessage response = await _http
                .GetAsync("/api/tags", linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            // Confirm the configured model is actually installed locally.
            string body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            string wantedModel = NormalizeModelName(_settings.Model);
            bool modelPresent = body.Contains(wantedModel, StringComparison.OrdinalIgnoreCase);
            if (!modelPresent)
            {
                _log($"AI unavailable: model '{_settings.Model}' is not pulled locally (run: ollama pull {_settings.Model}).");
            }

            return modelPresent;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _log($"AI unavailable: could not reach local Ollama at {_settings.Endpoint} ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<AiExtractionResult> ExtractAsync(
        string documentPath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsLoopbackEndpoint())
        {
            return AiExtractionResult.Failure($"Endpoint '{_settings.Endpoint}' is not loopback; extraction refused.");
        }

        try
        {
            IReadOnlyList<byte[]> pages = RenderPages(documentPath, password);
            if (pages.Count == 0)
            {
                return AiExtractionResult.Failure("No pages could be rendered from the document.");
            }

            var rows = new List<AiExtractionRow>();
            int pageNumber = 0;
            foreach (byte[] pngBytes in pages)
            {
                pageNumber++;
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<AiExtractionRow> pageRows =
                    await ExtractPageAsync(pngBytes, cancellationToken).ConfigureAwait(false);

                _log($"AI page {pageNumber}: {pageRows.Count} row(s) extracted.");
                rows.AddRange(pageRows);
            }

            return AiExtractionResult.Success(rows);
        }
        catch (OperationCanceledException)
        {
            return AiExtractionResult.Failure("AI extraction was cancelled or timed out.");
        }
        catch (Exception ex)
        {
            return AiExtractionResult.Failure($"AI extraction failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<AiExtractionRow>> ExtractPageAsync(
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        var request = new OllamaGenerateRequest
        {
            Model = _settings.Model,
            Prompt = ExtractionPrompt,
            Images = [Convert.ToBase64String(pngBytes)],
            Stream = false,
            Format = "json",
            Options = new OllamaOptions { Temperature = _settings.Temperature },
        };

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync("/api/generate", request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log($"AI request failed with HTTP {(int)response.StatusCode}.");
            return Array.Empty<AiExtractionRow>();
        }

        OllamaGenerateResponse? payload = await response.Content
            .ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken)
            .ConfigureAwait(false);

        string? modelText = payload?.Response;
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return Array.Empty<AiExtractionRow>();
        }

        return ParseRows(modelText);
    }

    /// <summary>
    /// Renders every page of the document to PNG bytes. For images the file is used as-is; for PDFs
    /// each page is rasterized at high DPI. Capped by <see cref="AiSettings.MaxPages"/>.
    /// </summary>
    private IReadOnlyList<byte[]> RenderPages(string documentPath, string? password)
    {
        string extension = Path.GetExtension(documentPath).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp")
        {
            return [File.ReadAllBytes(documentPath)];
        }

        int pageCount = GetPdfPageCount(documentPath, password);
        int limit = Math.Min(pageCount, Math.Max(1, _settings.MaxPages));

        var pages = new List<byte[]>(limit);
        for (int page = 1; page <= limit; page++)
        {
            pages.Add(PdfPageRasterizer.RenderPageToPng(documentPath, page, password));
        }

        return pages;
    }

    private static int GetPdfPageCount(string pdfPath, string? password)
    {
        ParsingOptions options = string.IsNullOrEmpty(password)
            ? new ParsingOptions { UseLenientParsing = true }
            : new ParsingOptions { UseLenientParsing = true, Password = password };

        using PdfDocument document = PdfDocument.Open(pdfPath, options);
        return document.NumberOfPages;
    }

    /// <summary>
    /// Parses the model's JSON text into rows. Tolerant of surrounding whitespace and of the model
    /// wrapping the array in an object (e.g. { "transactions": [ ... ] }). Never throws.
    /// </summary>
    internal static IReadOnlyList<AiExtractionRow> ParseRows(string modelText)
    {
        string json = ExtractJsonPayload(modelText);
        if (json.Length == 0)
        {
            return Array.Empty<AiExtractionRow>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            JsonElement array = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object => FindFirstArray(root),
                _ => default,
            };

            if (array.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<AiExtractionRow>();
            }

            var rows = new List<AiExtractionRow>();
            foreach (JsonElement element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var row = new AiExtractionRow(
                    ReadString(element, "date"),
                    ReadString(element, "description"),
                    ReadString(element, "debit"),
                    ReadString(element, "credit"),
                    ReadString(element, "balance"));

                // Ignore fully-empty rows the model may emit.
                if (!string.IsNullOrWhiteSpace(row.Date) ||
                    !string.IsNullOrWhiteSpace(row.Description) ||
                    !string.IsNullOrWhiteSpace(row.Debit) ||
                    !string.IsNullOrWhiteSpace(row.Credit) ||
                    !string.IsNullOrWhiteSpace(row.Balance))
                {
                    rows.Add(row);
                }
            }

            return rows;
        }
        catch (JsonException)
        {
            return Array.Empty<AiExtractionRow>();
        }
    }

    private static JsonElement FindFirstArray(JsonElement obj)
    {
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value;
            }
        }

        return default;
    }

    private static string ReadString(JsonElement obj, string key)
    {
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => string.Empty,
                };
            }
        }

        return string.Empty;
    }

    /// <summary>Extracts the first JSON array/object substring from arbitrary model text.</summary>
    private static string ExtractJsonPayload(string text)
    {
        string trimmed = text.Trim();

        // Strip common markdown code fences if the model added them despite instructions.
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            int fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                trimmed = trimmed[..fenceEnd];
            }

            trimmed = trimmed.Trim();
        }

        int arrayStart = trimmed.IndexOf('[');
        int objectStart = trimmed.IndexOf('{');
        int start = (arrayStart, objectStart) switch
        {
            (< 0, < 0) => -1,
            (< 0, _) => objectStart,
            (_, < 0) => arrayStart,
            _ => Math.Min(arrayStart, objectStart),
        };

        if (start < 0)
        {
            return string.Empty;
        }

        char open = trimmed[start];
        char close = open == '[' ? ']' : '}';
        int end = trimmed.LastIndexOf(close);
        if (end <= start)
        {
            return string.Empty;
        }

        return trimmed[start..(end + 1)];
    }

    private static string NormalizeModelName(string model)
    {
        // Ollama tags may include an explicit ":latest"; match on the base name for robustness.
        int colon = model.IndexOf(':');
        return colon > 0 ? model[..colon] : model;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; init; } = string.Empty;

        [JsonPropertyName("images")]
        public string[] Images { get; init; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("format")]
        public string Format { get; init; } = "json";

        [JsonPropertyName("options")]
        public OllamaOptions Options { get; init; } = new();
    }

    private sealed class OllamaOptions
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; init; }
    }
}

using System.Globalization;
using Taxation.StatementParser.Console.Configuration;
using Taxation.StatementParser.Console.Excel;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;
using Taxation.StatementParser.Console.Parsing.Ai;

namespace Taxation.StatementParser.Console.Processing;

/// <summary>
/// Shared statement-processing pipeline used by both the console and web front ends.
/// Encapsulates path normalization, validation, PDF parsing, accumulation and Excel writing so
/// there is a single authoritative implementation and no behavioural drift between UIs.
/// </summary>
public sealed class StatementProcessingService
{
    /// <summary>Application settings (Excel password protection) loaded from appsettings.json.</summary>
    private readonly AppSettings _settings;

    /// <summary>
    /// Factory for the local AI extractor. Overridable so tests can inject a mock without a running
    /// Ollama service. Defaults to the offline <see cref="OllamaVisionExtractor"/>.
    /// </summary>
    private readonly Func<AiSettings, Action<string>?, IAiStatementExtractor> _aiExtractorFactory;

    /// <summary>Canonical output columns used when AI supplies the transactions.</summary>
    private static readonly string[] AiColumns =
        ["Transaction Date", "Description", "Debit", "Credit", "Balance"];

    /// <summary>Creates the service using settings loaded from <c>appsettings.json</c>.</summary>
    public StatementProcessingService()
        : this(AppSettings.Load())
    {
    }

    /// <summary>Creates the service with explicit settings (used by tests).</summary>
    public StatementProcessingService(AppSettings settings)
        : this(settings, static (aiSettings, log) => new OllamaVisionExtractor(aiSettings, log))
    {
    }

    /// <summary>Creates the service with explicit settings and an AI extractor factory (used by tests).</summary>
    public StatementProcessingService(
        AppSettings settings,
        Func<AiSettings, Action<string>?, IAiStatementExtractor> aiExtractorFactory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _aiExtractorFactory = aiExtractorFactory ?? throw new ArgumentNullException(nameof(aiExtractorFactory));
    }

    /// <summary>Image formats that are OCR'd for scanned / captured statements.</summary>
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"];

    /// <summary>All file extensions the tool can process (PDF plus scanned image formats).</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [".pdf", .. ImageExtensions];

    /// <summary>True when <paramref name="path"/> points at a scanned/captured image (not a PDF).</summary>
    public static bool IsImagePath(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the PDF at <paramref name="pdfPath"/> by automatically detecting its columns from the
    /// statement header (no column names required), accumulates the transactions and writes an Excel
    /// workbook next to the source PDF. When the statement has both a Transaction Date and a Value
    /// Date, the Transaction Date is always used.
    /// </summary>
    /// <param name="pdfPath">Path to the source PDF (may include quotes, env vars, spaces).</param>
    /// <param name="log">Optional diagnostic sink.</param>
    public StatementProcessingResult Process(string pdfPath, Action<string>? log = null, string? password = null)
    {
        string path = NormalizePath(pdfPath ?? string.Empty);
        StatementProcessingResult? pathError = ValidateInputPath(path);
        if (pathError is not null)
        {
            return pathError;
        }

        try
        {
            var parser = new PdfStatementParser(log, password);
            IReadOnlyList<StatementTransaction> transactions;
            IReadOnlyList<string> columns;

            // Diagnostic captured when the geometric engine throws, so we can report a precise
            // reason if the AI fallback is unavailable or also unable to recover.
            string? geometricError = null;

            try
            {
                transactions = IsImagePath(path)
                    ? parser.ParseImage(path)
                    : parser.Parse(path);
                columns = parser.Columns;
            }
            catch (Exception ex) when (_settings.Ai.IsEnabled)
            {
                // Geometric parsing threw — e.g. a new/slightly different statement layout whose
                // header or columns the geometric engine did not recognise. Instead of failing, we
                // fall through to the AI fallback below to recover the transactions.
                geometricError = ex.Message;
                log?.Invoke($"Geometric parser failed ({ex.Message}); attempting AI fallback.");
                transactions = [];
                columns = [];
            }

            // Optional, fully-offline AI fallback. Runs only when enabled AND (configured to always
            // run, OR the input is a scan/image, OR the geometric result threw / is empty / fails the
            // balance-continuity sanity check — i.e. an unrecognised or slightly-changed layout).
            // If the local runtime is unavailable the geometric result is kept unchanged.
            if (ShouldTryAi(path, columns, transactions))
            {
                if (geometricError is null)
                {
                    log?.Invoke(transactions.Count == 0
                        ? "Geometric parser produced no transactions; attempting AI fallback."
                        : "Geometric parse result failed validation; attempting AI fallback.");
                }

                (IReadOnlyList<string> aiColumns, IReadOnlyList<StatementTransaction> aiTransactions) =
                    TryAiExtraction(path, password, log);

                if (aiTransactions.Count > 0 &&
                    (transactions.Count == 0 || StatementValidation.LooksValid(aiColumns, aiTransactions)))
                {
                    log?.Invoke($"AI extraction accepted: {aiTransactions.Count} transaction(s).");
                    columns = aiColumns;
                    transactions = aiTransactions;
                }
            }

            // If the geometric engine threw and AI could not recover (disabled, unavailable, or its
            // output was rejected), surface a clear, actionable failure rather than a generic one.
            if (columns.Count < 2)
            {
                if (geometricError is not null)
                {
                    string hint = _settings.Ai.IsEnabled
                        ? "The AI fallback could not recover the transactions (the local AI model may " +
                          "be offline or the layout unreadable)."
                        : "Enable the local AI fallback (set Ai:Enabled to \"yes\" in appsettings.json) " +
                          "to automatically handle new or slightly different statement layouts.";

                    return StatementProcessingResult.Failure(
                        $"Could not parse this statement layout: {geometricError} {hint}");
                }

                return StatementProcessingResult.Failure(
                    "Could not detect the statement columns automatically. Ensure the file contains a " +
                    "table with a Date column, and that a scanned document is clear and upright.");
            }

            return Finalize(columns, transactions, path, parser.AccountInfo);
        }
        catch (Exception ex)
        {
            return StatementProcessingResult.Failure($"Operation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the PDF at <paramref name="pdfPath"/> using the supplied column names, accumulates
    /// the transactions and writes an Excel workbook next to the source PDF.
    /// </summary>
    /// <param name="rawColumns">Ordered column names exactly as they appear in the statement.</param>
    /// <param name="pdfPath">Path to the source PDF (may include quotes, env vars, spaces).</param>
    /// <param name="log">Optional diagnostic sink.</param>
    public StatementProcessingResult Process(
        IReadOnlyList<string> rawColumns,
        string pdfPath,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(rawColumns);

        IReadOnlyList<string> columns = NormalizeColumns(rawColumns);
        if (columns.Count < 2)
        {
            return StatementProcessingResult.Failure("At least two distinct column names are required.");
        }

        if (columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count)
        {
            return StatementProcessingResult.Failure("Duplicate column names are not allowed.");
        }

        string path = NormalizePath(pdfPath ?? string.Empty);
        StatementProcessingResult? pathError = ValidateInputPath(path);
        if (pathError is not null)
        {
            return pathError;
        }

        try
        {
            var parser = new PdfStatementParser(columns, anchorColumnIndex: 0, log: log);
            IReadOnlyList<StatementTransaction> transactions = IsImagePath(path)
                ? parser.ParseImage(path)
                : parser.Parse(path);

            return Finalize(columns, transactions, path, parser.AccountInfo);
        }
        catch (Exception ex)
        {
            return StatementProcessingResult.Failure($"Operation failed: {ex.Message}");
        }
    }

    private static StatementProcessingResult? ValidateInputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return StatementProcessingResult.Failure("A statement file path is required.");
        }

        if (!File.Exists(path))
        {
            return StatementProcessingResult.Failure($"File not found: {path}");
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            return StatementProcessingResult.Failure(
                "The selected file type is not supported. Choose a PDF or a scanned image " +
                "(PNG, JPG, JPEG, TIF, TIFF, BMP).");
        }

        return null;
    }

    /// <summary>
    /// Decides whether the optional AI extractor should run: only when enabled AND either configured
    /// to always run, the input is a scanned/image file, or the geometric parse yielded no columns,
    /// no transactions, or a result that fails the balance-continuity sanity check.
    /// </summary>
    private bool ShouldTryAi(
        string path,
        IReadOnlyList<string> columns,
        IReadOnlyList<StatementTransaction> transactions)
    {
        if (!_settings.Ai.IsEnabled)
        {
            return false;
        }

        if (_settings.Ai.IsAlwaysMode || IsImagePath(path))
        {
            return true;
        }

        return columns.Count < 2
            || transactions.Count == 0
            || !StatementValidation.LooksValid(columns, transactions);
    }

    /// <summary>
    /// Runs the local, offline AI extractor for the document. Returns empty results (never throws)
    /// when the runtime is unavailable or produces nothing, so the caller keeps the geometric result.
    /// </summary>
    private (IReadOnlyList<string> Columns, IReadOnlyList<StatementTransaction> Transactions) TryAiExtraction(
        string path,
        string? password,
        Action<string>? log)
    {
        try
        {
            IAiStatementExtractor extractor = _aiExtractorFactory(_settings.Ai, log);
            try
            {
                if (!extractor.IsAvailableAsync().GetAwaiter().GetResult())
                {
                    log?.Invoke("AI extractor is not available; keeping geometric parse result.");
                    return ([], []);
                }

                AiExtractionResult result = extractor
                    .ExtractAsync(path, password)
                    .GetAwaiter()
                    .GetResult();

                if (!result.Succeeded)
                {
                    log?.Invoke($"AI extraction did not succeed: {result.Diagnostic}");
                    return ([], []);
                }

                IReadOnlyList<StatementTransaction> transactions = ConvertAiRows(result.Rows);
                return (AiColumns, transactions);
            }
            finally
            {
                (extractor as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"AI extraction error (ignored, using geometric result): {ex.Message}");
            return ([], []);
        }
    }

    /// <summary>Maps AI rows to <see cref="StatementTransaction"/> objects on the canonical columns.</summary>
    private static IReadOnlyList<StatementTransaction> ConvertAiRows(IReadOnlyList<AiExtractionRow> rows)
    {
        var transactions = new List<StatementTransaction>(rows.Count);
        foreach (AiExtractionRow row in rows)
        {
            var transaction = new StatementTransaction(AiColumns);
            transaction.SetColumn("Transaction Date", row.Date);
            transaction.SetColumn("Description", row.Description);
            transaction.SetColumn("Debit", row.Debit);
            transaction.SetColumn("Credit", row.Credit);
            transaction.SetColumn("Balance", row.Balance);

            if (!transaction.IsEmpty)
            {
                transactions.Add(transaction);
            }
        }

        return transactions;
    }

    private StatementProcessingResult Finalize(
        IReadOnlyList<string> columns,
        IReadOnlyList<StatementTransaction> transactions,
        string path,
        StatementAccountInfo accountInfo)
    {
        if (transactions.Count == 0)
        {
            return StatementProcessingResult.Failure(
                "No transactions were detected. Please verify the statement contains a recognisable table.");
        }

        IReadOnlyList<ColumnType> columnTypes = ColumnClassifier.ClassifyAll(columns);

        string outputPath = BuildOutputPath(path);
        AccumulationResult accumulation = AccumulationEngine.Build(columns, columnTypes, transactions);

        // Apply the configured Excel protection: encrypt only when enabled AND a non-empty password
        // is configured. A null/empty password tells the writer to leave the workbook unprotected.
        string? workbookPassword =
            _settings.ExcelProtection.IsProtectionEnabled && !string.IsNullOrWhiteSpace(_settings.ExcelProtection.Password)
                ? _settings.ExcelProtection.Password
                : null;

        new ExcelStatementWriter().Write(
            outputPath, columns, transactions, columnTypes, accumulation, workbookPassword, accountInfo);

        return StatementProcessingResult.Success(
            outputPath,
            transactions.Count,
            accumulation.Groups.Count,
            columns,
            columnTypes,
            accountInfo);
    }

    private static IReadOnlyList<string> NormalizeColumns(IReadOnlyList<string> rawColumns) =>
        rawColumns
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToList();

    private static string BuildOutputPath(string pdfPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(pdfPath)) ?? Directory.GetCurrentDirectory();
        string baseName = Path.GetFileNameWithoutExtension(pdfPath);
        string readableDate = DateTime.Now.ToString("dd MMM yyyy 'at' HH.mm", CultureInfo.InvariantCulture);
        return Path.Combine(directory, $"{baseName} - Parsed Statement ({readableDate}).xlsx");
    }

    /// <summary>
    /// Cleans a user supplied file path so it works regardless of spaces, surrounding quotes,
    /// environment variables or relative segments. Spaces inside the path are preserved.
    /// </summary>
    public static string NormalizePath(string input)
    {
        string path = input.Trim();

        if (path.Length >= 2 &&
            ((path[0] == '"' && path[^1] == '"') || (path[0] == '\'' && path[^1] == '\'')))
        {
            path = path[1..^1];
        }

        path = path.Trim();

        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        path = Environment.ExpandEnvironmentVariables(path);

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

/// <summary>Outcome of a statement processing run.</summary>
public sealed record StatementProcessingResult
{
    private StatementProcessingResult(bool ok, string? error)
    {
        Ok = ok;
        Error = error;
    }

    public bool Ok { get; private init; }

    public string? Error { get; private init; }

    public string? OutputPath { get; private init; }

    public int TransactionCount { get; private init; }

    public int GroupCount { get; private init; }

    public IReadOnlyList<string> Columns { get; private init; } = [];

    public IReadOnlyList<ColumnType> ColumnTypes { get; private init; } = [];

    public StatementAccountInfo AccountInfo { get; private init; } = StatementAccountInfo.Empty;

    public static StatementProcessingResult Failure(string error) => new(false, error);

    public static StatementProcessingResult Success(
        string outputPath,
        int transactionCount,
        int groupCount,
        IReadOnlyList<string> columns,
        IReadOnlyList<ColumnType> columnTypes,
        StatementAccountInfo accountInfo) =>
        new(true, null)
        {
            OutputPath = outputPath,
            TransactionCount = transactionCount,
            GroupCount = groupCount,
            Columns = columns,
            ColumnTypes = columnTypes,
            AccountInfo = accountInfo,
        };
}

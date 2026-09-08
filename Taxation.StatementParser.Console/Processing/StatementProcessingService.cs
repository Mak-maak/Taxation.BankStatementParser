using System.Globalization;
using Taxation.StatementParser.Console.Configuration;
using Taxation.StatementParser.Console.Excel;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;

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

    /// <summary>Creates the service using settings loaded from <c>appsettings.json</c>.</summary>
    public StatementProcessingService()
        : this(AppSettings.Load())
    {
    }

    /// <summary>Creates the service with explicit settings (used by tests).</summary>
    public StatementProcessingService(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
            IReadOnlyList<StatementTransaction> transactions = IsImagePath(path)
                ? parser.ParseImage(path)
                : parser.Parse(path);
            IReadOnlyList<string> columns = parser.Columns;

            if (columns.Count < 2)
            {
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

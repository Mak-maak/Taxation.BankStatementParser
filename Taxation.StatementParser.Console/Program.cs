using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Processing;
using Taxation.StatementParser.Console.Web;

// The application launches a clean local web UI by default. Pass --console to use the classic
// text prompts instead (useful for automation or headless environments).
bool useConsole = args.Any(a =>
    string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(a, "-c", StringComparison.OrdinalIgnoreCase));

return useConsole ? RunConsole() : new StatementWebServer().Run();

static int RunConsole()
{
    PrintBanner();

    string pdfPath = PromptForPdfPath();

    Console.WriteLine();
    Console.WriteLine("Parsing statement... columns are detected automatically from the statement header.");

    StatementProcessingResult result = new StatementProcessingService().Process(pdfPath, WriteDiagnostic);

    Console.WriteLine();
    if (result.Ok)
    {
        PrintDetectedTypes(result.Columns, result.ColumnTypes);
        Console.WriteLine();
        WriteSuccess($"Done. {result.TransactionCount} transaction(s) written to:");
        Console.WriteLine($"  {result.OutputPath}");
        Console.WriteLine($"  Accumulated into {result.GroupCount} group(s) on the 'Accumulated' sheet.");
        return 0;
    }

    WriteError(result.Error ?? "Operation failed.");
    return 1;
}

static string PromptForPdfPath()
{
    while (true)
    {
        Console.WriteLine();
        Console.Write("Enter the full path to the bank statement (PDF or scanned image): ");
        string? input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
        {
            WriteWarning("A file path is required. Please try again.");
            continue;
        }

        // Normalize the path so that spaces, surrounding quotes, environment variables and
        // relative paths are all handled robustly.
        string path = StatementProcessingService.NormalizePath(input);

        if (string.IsNullOrWhiteSpace(path))
        {
            WriteWarning("A file path is required. Please try again.");
            continue;
        }

        if (!File.Exists(path))
        {
            WriteWarning($"File not found: {path}. Please try again.");
            continue;
        }

        if (!StatementProcessingService.SupportedExtensions.Contains(
                Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            WriteWarning(
                "Unsupported file type. Provide a PDF or a scanned image (PNG, JPG, JPEG, TIF, TIFF, BMP).");
            continue;
        }

        return path;
    }
}

static void PrintBanner()
{
    Console.WriteLine("========================================================");
    Console.WriteLine("  Bank Statement (PDF / Scanned Image) -> Excel Parser");
    Console.WriteLine("========================================================");
}

static void PrintDetectedTypes(IReadOnlyList<string> columns, IReadOnlyList<ColumnType> types)
{
    Console.WriteLine();
    Console.WriteLine("Detected column data types (used for Excel formatting):");
    for (int i = 0; i < columns.Count; i++)
    {
        Console.WriteLine($"  - {columns[i]} => {types[i]}");
    }
}

static void WriteDiagnostic(string message) => WriteColored($"  [info] {message}", ConsoleColor.DarkGray);
static void WriteSuccess(string message) => WriteColored(message, ConsoleColor.Green);
static void WriteWarning(string message) => WriteColored(message, ConsoleColor.Yellow);
static void WriteError(string message) => WriteColored(message, ConsoleColor.Red);

static void WriteColored(string message, ConsoleColor color)
{
    ConsoleColor original = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = original;
}

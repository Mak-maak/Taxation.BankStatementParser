using System.Text;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing.Ocr;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// Parses a bank statement PDF into a list of <see cref="StatementTransaction"/> using the
/// geometric (X/Y coordinate) layout of the text rather than fragile string splitting.
///
/// Strategy (designed for maximum reliability on financial documents):
///   1. Extract every word on every page together with its bounding box.
///   2. Cluster words into physical text lines using their vertical position.
///   3. Locate the header line by matching the user supplied column names, and from the
///      header word positions derive the horizontal (X) boundaries of each column.
///   4. Assign each data word to a column based on its horizontal centre.
///   5. Treat a line as a NEW transaction only when the anchor column (first column, e.g. Date)
///      contains data; otherwise merge the line into the previous transaction. This is what
///      correctly keeps a multi line description as a single transaction.
/// </summary>
public sealed class PdfStatementParser
{
    private IReadOnlyList<string> _columns;
    private int _anchorColumnIndex;
    private readonly bool _autoDetect;
    private readonly Action<string> _log;
    private readonly string? _password;
    private IOcrTextExtractor? _ocr;

    /// <param name="columns">Ordered column names (as they appear in the statement header).</param>
    /// <param name="anchorColumnIndex">
    /// Index of the column whose presence marks the start of a new transaction (default: first column).
    /// </param>
    /// <param name="log">Optional diagnostics sink.</param>
    /// <param name="password">Optional password for encrypted (password protected) PDFs.</param>
    public PdfStatementParser(
        IReadOnlyList<string> columns,
        int anchorColumnIndex = 0,
        Action<string>? log = null,
        string? password = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count < 2)
        {
            throw new ArgumentException("At least two columns are required to parse a statement table.", nameof(columns));
        }

        if (anchorColumnIndex < 0 || anchorColumnIndex >= columns.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorColumnIndex));
        }

        _columns = columns;
        _anchorColumnIndex = anchorColumnIndex;
        _autoDetect = false;
        _log = log ?? (_ => { });
        _password = password;
    }

    /// <summary>
    /// Creates a parser that automatically discovers the statement's columns from the PDF header,
    /// so the caller does not need to supply column names. The Transaction Date is chosen as the
    /// anchor column; when a statement also has a Value Date, the Transaction Date is always used.
    /// </summary>
    /// <param name="log">Optional diagnostics sink.</param>
    /// <param name="password">Optional password for encrypted (password protected) PDFs.</param>
    public PdfStatementParser(Action<string>? log = null, string? password = null)
    {
        _columns = [];
        _anchorColumnIndex = 0;
        _autoDetect = true;
        _log = log ?? (_ => { });
        _password = password;
    }

    /// <summary>
    /// The columns the parser worked against. In automatic mode this is populated once the header
    /// has been detected during <see cref="Parse"/>.
    /// </summary>
    public IReadOnlyList<string> Columns => _columns;

    /// <summary>Index of the anchor (Transaction Date) column within <see cref="Columns"/>.</summary>
    public int AnchorColumnIndex => _anchorColumnIndex;

    /// <summary>
    /// Account level metadata (title, account number, IBAN, statement period) extracted from the
    /// statement text during <see cref="Parse"/>. Empty until the PDF has been parsed.
    /// </summary>
    public StatementAccountInfo AccountInfo { get; private set; } = StatementAccountInfo.Empty;

    public IReadOnlyList<StatementTransaction> Parse(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            throw new ArgumentException("PDF path must be provided.", nameof(pdfPath));
        }

        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException("The specified PDF file was not found.", pdfPath);
        }

        return ParsePages(EnumeratePdfPages(pdfPath), isImage: false);
    }

    /// <summary>
    /// Parses a scanned/captured statement supplied as an image file (PNG, JPG, JPEG, TIF, TIFF, BMP).
    /// The image is OCR'd into positioned words and then flows through the exact same geometric
    /// column/transaction pipeline used for PDFs.
    /// </summary>
    public IReadOnlyList<StatementTransaction> ParseImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));
        }

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("The specified image file was not found.", imagePath);
        }

        return ParsePages(EnumerateImagePages(imagePath), isImage: true);
    }

    /// <summary>Yields the physical text lines of each PDF page, using OCR as a per-page fallback.</summary>
    private IEnumerable<List<TextLine>> EnumeratePdfPages(string pdfPath)
    {
        using PdfDocument document = OpenDocument(pdfPath);
        int pageNumber = 0;

        foreach (Page page in document.GetPages())
        {
            pageNumber++;
            IEnumerable<PositionedWord> pageWords = page
                .GetWords(NearestNeighbourWordExtractor.Instance)
                .Select(PositionedWord.FromPdfWord);
            List<TextLine> lines = BuildLines(pageWords);
            if (lines.Count == 0)
            {
                // No selectable text: this is a scanned/image-only page. Fall back to OCR by
                // rasterizing the page to an image and reading the words (with bounding boxes)
                // from it, then run the very same geometric pipeline on those words.
                _log($"Page {pageNumber}: no extractable text; falling back to OCR (scanned page).");
                lines = BuildLines(OcrPage(pdfPath, pageNumber));
                if (lines.Count == 0)
                {
                    _log($"Page {pageNumber}: OCR produced no text.");
                }
            }

            yield return lines;
        }
    }

    /// <summary>Yields the single OCR'd page produced from a standalone statement image.</summary>
    private IEnumerable<List<TextLine>> EnumerateImagePages(string imagePath)
    {
        _ocr ??= new PaddleOcrTextExtractor();
        _log("Running OCR on the supplied image...");
        yield return BuildLines(_ocr.ExtractFromImageFile(imagePath));
    }

    private IReadOnlyList<StatementTransaction> ParsePages(IEnumerable<List<TextLine>> pages, bool isImage)
    {
        var transactions = new List<StatementTransaction>();
        var documentLines = new List<string>();
        StatementTransaction? current = null;
        ColumnLayout? layout = null;
        bool headerEverFound = false;
        int pageNumber = 0;

        foreach (List<TextLine> lines in pages)
        {
            pageNumber++;
            if (lines.Count == 0)
            {
                continue;
            }

            // Retain the raw line text for account-level metadata extraction (title/number/IBAN/period).
            documentLines.AddRange(lines.Select(l => l.Text));

            int headerIndex = DetectHeader(lines, out ColumnLayout? pageLayout);
            if (pageLayout is not null)
            {
                layout = pageLayout;
                headerEverFound = true;
                _log($"Page {pageNumber}: header detected on line {headerIndex + 1} " +
                     $"({string.Join(" | ", _columns)}); anchor date column: '{_columns[_anchorColumnIndex]}'.");
            }
            else if (layout is null)
            {
                _log($"Page {pageNumber}: header not found and no prior layout; skipping page.");
                continue;
            }

            int startLine = headerIndex >= 0 ? headerIndex + 1 : 0;
            for (int i = startLine; i < lines.Count; i++)
            {
                TextLine line = lines[i];

                // Skip a repeated header on continuation pages.
                if (IsHeaderLine(line))
                {
                    continue;
                }

                string[] cells = AssignCells(line, layout!);
                if (cells.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }
                // The anchor column is the Date column. A line begins a NEW transaction ONLY when
                // its anchor cell contains a genuine, normalizable calendar date. This is what
                // rejects page footers/print stamps ("22 Aug 2026, 16:51") and stray text
                // ("ANWAR", "HBL", "to") that would otherwise be mistaken for transaction rows.
                bool anchorIsDate = DateColumnNormalizer.TryNormalize(
                    cells[_anchorColumnIndex],
                    out string normalizedDate,
                    out string trailingNoise);

                if (anchorIsDate)
                {
                    // Store the date in one canonical format and push any noise that bled into the
                    // date cell (e.g. "03 Jul 2025 Money") into the description column instead.
                    cells[_anchorColumnIndex] = normalizedDate;
                    if (trailingNoise.Length > 0)
                    {
                        int descriptionColumn = FirstNonAnchorColumn();
                        cells[descriptionColumn] = string.IsNullOrWhiteSpace(cells[descriptionColumn])
                            ? trailingNoise
                            : $"{trailingNoise} {cells[descriptionColumn]}".Trim();
                    }

                    current = new StatementTransaction(_columns);
                    for (int c = 0; c < _columns.Count; c++)
                    {
                        current.SetColumn(_columns[c], cells[c]);
                    }

                    transactions.Add(current);
                }
                else if (!string.IsNullOrWhiteSpace(cells[_anchorColumnIndex]))
                {
                    // Anchor cell has content but is NOT a valid date: this is a footer or stray
                    // artifact, never a transaction. Discard it so it cannot corrupt the output.
                    _log($"Discarded non-date anchor line: '{cells[_anchorColumnIndex]}'.");
                    continue;
                }
                else if (current is not null)
                {
                    // Continuation line (e.g. wrapped description): merge into the current transaction.
                    for (int c = 0; c < _columns.Count; c++)
                    {
                        current.AppendToColumn(_columns[c], cells[c]);
                    }
                }
            }
        }

        _ocr?.Dispose();
        _ocr = null;

        if (!headerEverFound)
        {
            throw new InvalidOperationException(
                BuildHeaderNotFoundMessage(isImage));
        }

        var cleaned = transactions.Where(t => !t.IsEmpty).ToList();

        string? dateColumn = _columns.Count > 0 && _anchorColumnIndex < _columns.Count
            ? _columns[_anchorColumnIndex]
            : null;
        AccountInfo = StatementMetadataExtractor.Extract(documentLines, cleaned, dateColumn);

        _log($"Parsing complete: {cleaned.Count} transaction(s) extracted from {pageNumber} page(s).");
        return cleaned;
    }

    private string BuildHeaderNotFoundMessage(bool isImage)
    {
        if (isImage)
        {
            return "Could not detect the statement table header in the image. Ensure the scan/photo is " +
                   "clear, upright and shows a table with a Date column and recognisable headings " +
                   "(e.g. Description, Debit, Credit, Balance).";
        }

        return _autoDetect
            ? "Could not automatically detect the statement table header in the PDF. " +
              "Ensure the file contains selectable text (not a scanned image) and a table with " +
              "a Date column and recognisable headings (e.g. Description, Debit, Credit, Balance)."
            : "Could not locate a header row matching the supplied column names in the PDF. " +
              "Verify the column names exactly match the statement headings (spelling/order), " +
              "or that the PDF contains selectable text (not a scanned image).";
    }

    /// <summary>
    /// Opens the PDF, supplying the supplied password when the document is encrypted. An empty
    /// password is always tried as well, which transparently handles files that are encrypted with
    /// empty owner/user passwords. Throws a clear error when a password is required but missing or
    /// incorrect.
    /// </summary>
    private PdfDocument OpenDocument(string pdfPath)
    {
        var options = new ParsingOptions
        {
            // Try the caller supplied password first, then the empty password.
            Passwords = string.IsNullOrEmpty(_password) ? [string.Empty] : [_password, string.Empty],
        };

        try
        {
            return PdfDocument.Open(pdfPath, options);
        }
        catch (Exception ex) when (IsEncryptionFailure(ex))
        {
            _log($"The PDF is password protected and the supplied password was {(string.IsNullOrEmpty(_password) ? "missing" : "incorrect")}.");
            throw new InvalidOperationException(
                string.IsNullOrEmpty(_password)
                    ? "This PDF is password protected. Please provide the password and try again."
                    : "The password provided for this protected PDF is incorrect. Please check it and try again.",
                ex);
        }
    }

    /// <summary>
    /// Recognises PdfPig's encryption/password failures without taking a hard compile-time dependency
    /// on a specific exception type name, which differs across PdfPig versions.
    /// </summary>
    private static bool IsEncryptionFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            string name = current.GetType().Name;
            if (name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// OCR fallback for a scanned/image-only page: rasterizes the page to an image and recognizes its
    /// words (with bounding boxes) so the standard geometric pipeline can process them unchanged.
    /// </summary>
    private IReadOnlyList<PositionedWord> OcrPage(string pdfPath, int pageNumber)
    {
        _ocr ??= new PaddleOcrTextExtractor();
        byte[] png = PdfPageRasterizer.RenderPageToPng(pdfPath, pageNumber, _password);
        return _ocr.ExtractFromImageBytes(png);
    }

    // ---------------------------------------------------------------------
    // Line clustering
    // ---------------------------------------------------------------------

    private static List<TextLine> BuildLines(IEnumerable<PositionedWord> words)
    {
        var lines = new List<TextLine>();

        // Iterate top-to-bottom (bottom-left origin, so larger Top == higher on page).
        foreach (PositionedWord word in words
                     .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                     .OrderByDescending(w => w.Top))
        {
            double centreY = (word.Top + word.Bottom) / 2.0;
            double height = Math.Abs(word.Height);
            double tolerance = Math.Max(height * 0.5, 2.0);

            TextLine? target = null;
            foreach (TextLine line in lines)
            {
                if (Math.Abs(line.CentreY - centreY) <= tolerance)
                {
                    target = line;
                    break;
                }
            }

            if (target is null)
            {
                target = new TextLine(centreY);
                lines.Add(target);
            }

            target.Add(word);
        }

        lines.Sort((a, b) => b.CentreY.CompareTo(a.CentreY));
        foreach (TextLine line in lines)
        {
            line.SortWordsLeftToRight();
        }

        return lines;
    }

    // ---------------------------------------------------------------------
    // Header / column boundary detection
    // ---------------------------------------------------------------------

    /// <summary>
    /// Detects the header on the current page. In automatic mode the columns are discovered from the
    /// page layout the first time a header is seen and then reused; in explicit mode the user supplied
    /// column names are matched.
    /// </summary>
    private int DetectHeader(List<TextLine> lines, out ColumnLayout? layout)
    {
        if (_autoDetect && _columns.Count == 0)
        {
            HeaderAutoDetector.Result? detected = HeaderAutoDetector.Detect(lines);
            if (detected is not null)
            {
                _columns = detected.Columns;
                _anchorColumnIndex = detected.AnchorColumnIndex;
                layout = ColumnLayout.FromHeaderExtents(detected.LayoutExtents, detected.OutputToLayoutColumn);
                return detected.HeaderLineIndex;
            }

            // Fallback for fixed-format scanned/photographed statements whose heading row is too
            // OCR-garbled for the word-based detector: anchor the layout geometrically from the data
            // rows (leading dates and right-aligned amounts), which OCR reproduces far more reliably.
            KnownScannedLayoutDetector.Result? known = KnownScannedLayoutDetector.Detect(lines);
            if (known is not null)
            {
                _columns = known.Columns;
                _anchorColumnIndex = known.AnchorColumnIndex;
                layout = known.Layout;
                return known.HeaderLineIndex;
            }

            layout = null;
            return -1;
        }

        return TryDetectHeader(lines, out layout);
    }

    private int TryDetectHeader(List<TextLine> lines, out ColumnLayout? layout)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var extents = new (double Left, double Right)?[_columns.Count];
            bool allMatched = true;
            for (int c = 0; c < _columns.Count; c++)
            {
                extents[c] = FindColumnExtent(lines[i], _columns[c]);
                if (extents[c] is null)
                {
                    allMatched = false;
                    break;
                }
            }

            if (allMatched)
            {
                (double Left, double Right)[] resolved = extents.Select(e => e!.Value).ToArray();
                layout = ColumnLayout.FromHeaderExtents(resolved);
                return i;
            }
        }

        layout = null;
        return -1;
    }

    private bool IsHeaderLine(TextLine line)
    {
        for (int c = 0; c < _columns.Count; c++)
        {
            if (FindColumnExtent(line, _columns[c]) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the horizontal extent of a (possibly multi word) column heading within a line.
    /// Matching is case/punctuation insensitive for robustness across bank formats.
    /// </summary>
    private static (double Left, double Right)? FindColumnExtent(TextLine line, string columnName)
    {
        string target = Normalize(columnName);
        if (target.Length == 0)
        {
            return null;
        }

        IReadOnlyList<PositionedWord> words = line.Words;
        for (int i = 0; i < words.Count; i++)
        {
            var builder = new StringBuilder();
            for (int j = i; j < words.Count; j++)
            {
                builder.Append(Normalize(words[j].Text));
                string combined = builder.ToString();

                if (string.Equals(combined, target, StringComparison.Ordinal))
                {
                    return (words[i].Left, words[j].Right);
                }

                if (!target.StartsWith(combined, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        return null;
    }

    private string[] AssignCells(TextLine line, ColumnLayout layout)
    {
        var builders = new StringBuilder[_columns.Count];
        for (int c = 0; c < _columns.Count; c++)
        {
            builders[c] = new StringBuilder();
        }

        foreach (PositionedWord word in line.Words)
        {
            double centreX = (word.Left + word.Right) / 2.0;
            int column = layout.ColumnForX(centreX);

            // -1 marks a word that fell under an ignored (non-canonical) heading; discard it so the
            // output is restricted to the canonical columns.
            if (column < 0)
            {
                continue;
            }

            if (builders[column].Length > 0)
            {
                builders[column].Append(' ');
            }

            builders[column].Append(word.Text);
        }

        var cells = new string[_columns.Count];
        for (int c = 0; c < _columns.Count; c++)
        {
            cells[c] = builders[c].ToString().Trim();
        }

        return cells;
    }

    /// <summary>The column that receives noise which bled into the date cell (first non-anchor column).</summary>
    private int FirstNonAnchorColumn()
    {
        for (int c = 0; c < _columns.Count; c++)
        {
            if (c != _anchorColumnIndex)
            {
                return c;
            }
        }

        return _anchorColumnIndex;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}

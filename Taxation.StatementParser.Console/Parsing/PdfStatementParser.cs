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

            // A new page (with its own detected header) starts a fresh transaction context. This
            // prevents a repeating per-page account banner (From/To Date, Statement/Account No,
            // masked account numbers) that sits ABOVE the header on the next page from ever being
            // merged as a continuation line into the last transaction of the previous page.
            if (headerIndex >= 0)
            {
                current = null;
            }

            for (int i = startLine; i < lines.Count; i++)
            {
                TextLine line = lines[i];

                // Skip a repeated header on continuation pages.
                if (IsHeaderLine(line))
                {
                    continue;
                }

                // Skip repeating account-banner / letterhead lines (From Date, To Date, Statement No,
                // Branch, Currency, Account No, IBAN, "electronic statement" footer). These carry dates
                // and masked numbers that would otherwise corrupt a transaction when merged.
                if (IsBannerLine(line))
                {
                    _log($"Skipped banner/metadata line: '{line.Text}'.");
                    continue;
                }

                string[] cells = AssignCells(line, layout!);
                if (cells.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                // "BALANCE B/F" (balance brought forward) carry-forward markers repeat at the top of
                // continuation pages and often lack a date. They are not real transactions; skip them
                // entirely so they neither create a spurious row nor merge into the previous one.
                if (IsBalanceCarriedForward(cells))
                {
                    _log($"Skipped balance brought-forward marker: '{line.Text}'.");
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

    // Distinctive labels that only ever appear in the repeating per-page account banner / letterhead,
    // never inside a transaction description. Kept intentionally specific (multi-word, unambiguous)
    // so ordinary descriptions such as "Opening balance" or "Salary" are never misclassified.
    private static readonly string[] BannerLabels =
    [
        "fromdate", "todate", "statementno", "statementdate", "statementperiod",
        "branchtel", "accountno", "accountnumber", "customerno", "customerid",
        "electronicstatement",
    ];

    /// <summary>
    /// Detects a repeating account-banner / letterhead / footer line so it can be skipped. Matching
    /// is punctuation/space insensitive so "From Date:", "From  Date", and "FROM DATE" all match.
    /// </summary>
    private static bool IsBannerLine(TextLine line)
    {
        string collapsed = Normalize(line.Text);
        if (collapsed.Length == 0)
        {
            return false;
        }

        foreach (string label in BannerLabels)
        {
            if (collapsed.Contains(label, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects a "Balance Brought Forward" carry-forward marker (e.g. "BALANCE B/F", "B/F",
    /// "Balance C/F", "Opening Balance") from the row's Description cell. These repeat at page
    /// boundaries and are not transactions, so the caller skips them. Matching is
    /// punctuation/space insensitive, so "BALANCE B/F", "Balance B / F" and "BF" all match.
    /// </summary>
    private bool IsBalanceCarriedForward(string[] cells)
    {
        int descriptionColumn = FirstNonAnchorColumn();
        if (descriptionColumn < 0 || descriptionColumn >= cells.Length)
        {
            return false;
        }

        string description = Normalize(cells[descriptionColumn]);
        if (description.Length == 0)
        {
            return false;
        }

        return description is "balancebf" or "balancecf"
            or "bf" or "cf"
            or "balancebroughtforward" or "balancecarriedforward";
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
        bool[] amountColumns = new bool[_columns.Count];
        for (int c = 0; c < _columns.Count; c++)
        {
            builders[c] = new StringBuilder();
            amountColumns[c] = ColumnClassifier.Classify(_columns[c]) == ColumnType.Amount;
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

            // Amount columns (Debit/Credit/Balance) must only ever contain monetary values. Discard
            // any stray text (e.g. a "Transaction De" hyperlink that geometrically overlaps the
            // balance column) OR digit-bearing but non-monetary tokens (reference numbers like
            // "01982518", value-date stamps like "V.010625", masked accounts like "*******8401")
            // so numeric cells are never polluted by non-amount words.
            if (amountColumns[column])
            {
                // A reference number glued to the real amount (no space) arrives as a single token
                // such as "8269717,500.00" (STAN "826971" + "7,500.00"). Repair it by extracting the
                // properly comma-grouped trailing amount; reject anything that is not a valid amount.
                if (!TryNormalizeAmountToken(word.Text, out string repaired))
                {
                    continue;
                }

                if (builders[column].Length > 0)
                {
                    builders[column].Append(' ');
                }

                builders[column].Append(repaired);
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

            // Safety net: an amount column must hold a SINGLE value. If more than one monetary token
            // survived (e.g. a comma-grouped reference like "1,234" drifting next to the real amount),
            // keep only the last (rightmost) one, since statement amounts are right-aligned.
            if (amountColumns[c] && cells[c].Contains(' '))
            {
                string[] parts = cells[c].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    cells[c] = parts[^1];
                }
            }
        }

        return cells;
    }

    private static bool ContainsDigit(string text)
    {
        foreach (char ch in text)
        {
            if (char.IsDigit(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates and repairs a token destined for an amount column. Returns the clean monetary
    /// value in <paramref name="normalized"/> when the token is (or contains) a genuine amount.
    ///
    /// A genuine amount is a run of digits optionally grouped by commas in exact 3-digit blocks with
    /// an optional 2-decimal fraction: "790.00", "7,500.00", "2,650", "20,000.00". This rejects:
    ///   - bare integers / references ("826971", "211638", "55051"),
    ///   - masked accounts and value stamps ("*******8401", "V.010625"),
    ///   - and REPAIRS a reference glued to an amount ("8269717,500.00" -> "7,500.00") by discarding
    ///     the malformed leading portion whose comma grouping is invalid.
    /// </summary>
    private static bool TryNormalizeAmountToken(string text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string token = text.Trim();

        // Strip a single leading currency/sign decoration and an optional trailing sign/marker.
        token = token.TrimStart('+', '(', '$', '£', '€', '₨');
        bool negative = token.StartsWith('-');
        token = token.TrimStart('-').TrimEnd(')', '-');

        if (token.Length == 0)
        {
            return false;
        }

        // Only digits, commas and a single dot may appear; anything else disqualifies the token.
        int dotCount = 0;
        foreach (char ch in token)
        {
            if (char.IsDigit(ch) || ch == ',')
            {
                continue;
            }

            if (ch == '.')
            {
                if (++dotCount > 1)
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        string integerPart = dotCount == 1 ? token[..token.IndexOf('.')] : token;
        string fractionPart = dotCount == 1 ? token[(token.IndexOf('.') + 1)..] : string.Empty;

        // Reject bare integers with no comma and no decimal: these are references, not amounts.
        if (dotCount == 0 && !token.Contains(','))
        {
            return false;
        }

        // Validate/repair the integer part's comma grouping. Correct grouping is a 1-3 digit lead
        // block followed by zero or more ",ddd" blocks. If the whole part is malformed (e.g.
        // "8269717,500" from a glued reference), keep only the valid rightmost grouped suffix.
        if (integerPart.Contains(','))
        {
            string[] groups = integerPart.Split(',');

            // Trailing groups must each be exactly 3 digits. Find the longest valid suffix.
            int firstValid = groups.Length; // index of first group that starts a valid suffix
            for (int g = groups.Length - 1; g >= 1; g--)
            {
                if (groups[g].Length == 3 && groups[g].All(char.IsDigit))
                {
                    firstValid = g;
                }
                else
                {
                    break;
                }
            }

            if (firstValid > groups.Length - 1)
            {
                // No valid trailing ",ddd" group at all -> not a real grouped amount.
                return false;
            }

            // The lead block (group just before the valid suffix) must be exactly 1-3 digits for a
            // correctly grouped amount. If it is longer (a glued reference such as "8269717"), the
            // token is unrecoverable garbage — reject it rather than inventing a wrong amount.
            string lead = groups[firstValid - 1];
            if (lead.Length is 0 or > 3 || !lead.All(char.IsDigit))
            {
                return false;
            }

            var rebuilt = new StringBuilder(lead);
            for (int g = firstValid; g < groups.Length; g++)
            {
                rebuilt.Append(',').Append(groups[g]);
            }

            integerPart = rebuilt.ToString();
        }

        if (integerPart.Length == 0 || !integerPart.Replace(",", string.Empty).All(char.IsDigit))
        {
            return false;
        }

        normalized = dotCount == 1 ? $"{integerPart}.{fractionPart}" : integerPart;
        if (negative)
        {
            normalized = "-" + normalized;
        }

        return true;
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

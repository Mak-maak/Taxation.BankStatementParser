using System.Text;
using Taxation.StatementParser.Console.Models;

namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// Automatically discovers the transaction table header of a bank statement so the user does not
/// have to type the column names by hand. Bank statements differ from one another, but their header
/// row is reliably recognisable: it is a single physical line that contains a Date heading together
/// with a small set of well known banking words (Description, Debit, Credit, Balance, Amount, ...).
///
/// The detector works purely from the geometric layout produced by <see cref="PdfStatementParser"/>:
///   1. For every physical line it groups words into columns using the horizontal gaps between them
///      (a wide gap separates two columns; a single space keeps a multi word heading together, e.g.
///      "Transaction Date").
///   2. It scores each line by how many of its column headings are recognised banking words and
///      requires at least one Date heading, which reliably distinguishes the header from data rows.
///   3. The best scoring line becomes the header, yielding the column names, their horizontal extents
///      (for <see cref="ColumnLayout"/>) and the anchor column.
///
/// Anchor selection honours the rule that when a statement carries two date columns (for example a
/// Transaction Date and a Value Date) the Transaction Date is always used to anchor a row.
/// </summary>
internal static class HeaderAutoDetector
{
    /// <summary>Words that, when present in a column heading, mark a line as a statement header.</summary>
    private static readonly HashSet<string> HeaderWords = new(StringComparer.Ordinal)
    {
        // Date related
        "date", "value", "transaction", "txn", "trans", "posting", "post", "booking", "book",
        "effective", "entry", "process", "processed",
        // Description related
        "description", "details", "particulars", "narration", "narrative", "remarks", "remark",
        "transactions", "reference", "ref", "chq", "cheque", "instrument", "memo", "type", "mode",
        "channel", "note", "notes", "doc",
        // Amount related
        "debit", "credit", "balance", "amount", "withdrawal", "withdrawals", "deposit", "deposits",
        "money", "payment", "payments", "paid", "in", "out", "dr", "cr", "closing", "opening",
        "running", "available",
    };

    /// <summary>
    /// Outcome of a successful header detection.
    ///
    /// The statement is restricted to the canonical banking columns
    /// (Transaction Date, Description, Ref/Cheque No, Debit, Credit, Balance); any other detected
    /// heading (e.g. Value Date, Bank/Branch, Serial No) is ignored. <see cref="Columns"/> therefore
    /// contains only the kept columns, while <see cref="LayoutExtents"/> retains the horizontal
    /// extents of every detected heading so word-to-column assignment stays geometrically accurate;
    /// <see cref="OutputToLayoutColumn"/> maps each kept output column back to its layout position.
    /// </summary>
    public sealed record Result(
        IReadOnlyList<string> Columns,
        (double Left, double Right)[] Extents,
        int AnchorColumnIndex,
        int HeaderLineIndex,
        (double Left, double Right)[] LayoutExtents,
        IReadOnlyList<int> OutputToLayoutColumn);

    /// <summary>The canonical banking column roles the parser keeps; everything else is ignored.</summary>
    private enum CanonicalRole
    {
        None = 0,
        TransactionDate,
        Description,
        Reference,
        Debit,
        Credit,
        Balance,
    }

    /// <summary>The fixed output order of the canonical columns.</summary>
    private static readonly CanonicalRole[] CanonicalOrder =
    [
        CanonicalRole.TransactionDate,
        CanonicalRole.Description,
        CanonicalRole.Reference,
        CanonicalRole.Debit,
        CanonicalRole.Credit,
        CanonicalRole.Balance,
    ];

    /// <summary>
    /// Attempts to locate the statement header amongst the supplied physical text lines.
    /// Returns <c>null</c> when no line looks like a transaction table header.
    /// </summary>
    public static Result? Detect(IReadOnlyList<TextLine> lines)
    {
        Result? best = null;
        int bestScore = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            List<ColumnGroup> groups = GroupIntoColumns(lines[i]);
            if (groups.Count < 2)
            {
                continue;
            }

            int score = 0;
            var dateColumns = new List<int>();
            for (int c = 0; c < groups.Count; c++)
            {
                string name = groups[c].Text;
                if (ContainsHeaderWord(name))
                {
                    score++;
                }

                if (ColumnClassifier.Classify(name) == ColumnType.Date)
                {
                    dateColumns.Add(c);
                }
            }

            // A header must contain a Date heading and at least two recognised headings overall;
            // this reliably separates the header from data rows (whose date cell is a bare date).
            if (dateColumns.Count == 0 || score < 2)
            {
                continue;
            }

            if (score > bestScore)
            {
                Result? restricted = RestrictToCanonicalColumns(lines[i], i);
                if (restricted is not null)
                {
                    best = restricted;
                    bestScore = score;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Reduces the detected header to the canonical banking columns
    /// (Transaction Date, Description, Ref/Cheque No, Debit, Credit, Balance) and ignores every other
    /// heading.
    ///
    /// Every header word becomes its own geometric LAYOUT column, so tightly packed sub-headings
    /// (e.g. "Tran. Date  Effect Date  Tran. Br.  Transaction Details  Remitter Name  Remitter IBAN
    /// Remitter Bank  Chq / Ref No") never merge into one wrong column. Consecutive words that name the
    /// same heading are joined (e.g. "Transaction" + "Details"). Each layout column is classified; the
    /// canonical ones are surfaced as OUTPUT columns while every other layout column is retained as an
    /// ignored boundary (its data is discarded by <see cref="ColumnLayout"/>). Retaining the ignored
    /// columns keeps word-to-column assignment geometrically accurate — for example a Value Date value
    /// lands in the (ignored) Value Date column instead of bleeding into the Transaction Date column.
    /// Returns <c>null</c> when the mandatory anchor Transaction Date column cannot be identified.
    /// </summary>
    private static Result? RestrictToCanonicalColumns(TextLine headerLine, int headerLineIndex)
    {
        // Group the header words into heading CELLS using the horizontal gaps between them (a wide gap
        // separates two headings; a normal space keeps a multi-word heading such as "Transaction Date"
        // or "Value Date" together). Each cell becomes one geometric layout column, classified purely
        // from its own tokens so neighbouring cells never influence one another (this correctly keeps
        // "Value Date" and "Transaction Date" as two distinct, separately-classified columns).
        List<ColumnGroup> groups = GroupIntoColumns(headerLine);
        if (groups.Count < 2)
        {
            return null;
        }

        var layout = new List<HeaderColumn>();
        foreach (ColumnGroup group in groups)
        {
            CanonicalRole role = ClassifyHeading(group.Text);
            layout.Add(new HeaderColumn(group.Text, group.Left, group.Right, role));
        }


        // For each canonical role keep the first (left-most) layout column; duplicate/ambiguous
        // headings never create extra output columns.
        var chosenForRole = new Dictionary<CanonicalRole, int>();
        for (int c = 0; c < layout.Count; c++)
        {
            CanonicalRole role = layout[c].Role;
            if (role != CanonicalRole.None && !chosenForRole.ContainsKey(role))
            {
                chosenForRole[role] = c;
            }
        }

        // The Transaction Date is mandatory: it anchors every row.
        if (!chosenForRole.ContainsKey(CanonicalRole.TransactionDate))
        {
            return null;
        }

        // Layout extents: every column (canonical and ignored) so geometry stays accurate.
        var layoutExtents = layout.Select(col => (col.Left, col.Right)).ToArray();

        // Output columns: the canonical roles in fixed order, each mapped back to its layout column.
        var outputNames = new List<string>();
        var outputToLayout = new List<int>();
        int anchorIndex = 0;

        foreach (CanonicalRole role in CanonicalOrder)
        {
            if (!chosenForRole.TryGetValue(role, out int layoutColumn))
            {
                continue;
            }

            if (role == CanonicalRole.TransactionDate)
            {
                anchorIndex = outputNames.Count;
            }

            outputNames.Add(CanonicalDisplayName(role));
            outputToLayout.Add(layoutColumn);
        }

        // A statement table needs the anchor plus at least one more meaningful column.
        if (outputNames.Count < 2)
        {
            return null;
        }

        (double Left, double Right)[] outputExtents =
            outputToLayout.Select(c => layoutExtents[c]).ToArray();

        return new Result(
            outputNames,
            outputExtents,
            anchorIndex,
            headerLineIndex,
            layoutExtents,
            outputToLayout);
    }

    private sealed record HeaderColumn(string Text, double Left, double Right, CanonicalRole Role);

    /// <summary>The canonical display name shown in the output for each kept column.</summary>
    private static string CanonicalDisplayName(CanonicalRole role) => role switch
    {
        CanonicalRole.TransactionDate => "Transaction Date",
        CanonicalRole.Description => "Description",
        CanonicalRole.Reference => "Ref/Cheque No",
        CanonicalRole.Debit => "Debit",
        CanonicalRole.Credit => "Credit",
        CanonicalRole.Balance => "Balance",
        _ => role.ToString(),
    };

    /// <summary>
    /// Classifies a single heading CELL (which may contain several tokens, e.g. "Transaction Date",
    /// "Money Out", "Chq / Ref No") into a canonical role, or <see cref="CanonicalRole.None"/> when it
    /// does not name a canonical column. Classification uses only the cell's own tokens so neighbouring
    /// cells never interfere (keeping "Value Date" and "Transaction Date" independent).
    /// </summary>
    private static CanonicalRole ClassifyHeading(string heading)
    {
        List<string> tokens = TokensOf(heading);
        if (tokens.Count == 0)
        {
            return CanonicalRole.None;
        }

        bool Has(params string[] words) => tokens.Any(words.Contains);

        // Amount columns.
        if (Has("balance", "closing", "running"))
        {
            return CanonicalRole.Balance;
        }

        // "Money Out" / "Paid Out" style debit headings.
        if (Has("money", "amount", "paid") && Has("out"))
        {
            return CanonicalRole.Debit;
        }

        if (Has("money", "amount", "paid") && Has("in"))
        {
            return CanonicalRole.Credit;
        }

        if (Has("debit", "withdrawal", "withdrawals", "dr") && !Has("credit"))
        {
            return CanonicalRole.Debit;
        }

        if (Has("credit", "deposit", "deposits", "cr") && !Has("debit"))
        {
            return CanonicalRole.Credit;
        }

        // Reference / cheque / instrument / document number.
        if (Has("ref", "reference", "chq", "cheque", "instrument", "doc"))
        {
            return CanonicalRole.Reference;
        }

        // Transaction Date anchors the row. A Value/Effect date is ignored — UNLESS the same cell also
        // carries an explicit transaction/posting qualifier (which can happen if two tightly-packed date
        // sub-headings were grouped together), in which case a real Transaction Date is present and wins.
        if (Has("date"))
        {
            bool isEffectDate = Has("value", "effect", "effective");
            bool isTransactionDate = Has("transaction", "txn", "tran", "trans", "posting",
                "post", "booking", "book", "entry");

            if (isEffectDate && !isTransactionDate)
            {
                return CanonicalRole.None;
            }

            return CanonicalRole.TransactionDate;
        }

        // Description / details / particulars / narration.
        if (Has("description", "details", "particulars", "narration", "narrative",
                "remarks", "remark", "memo"))
        {
            return CanonicalRole.Description;
        }

        return CanonicalRole.None;
    }

    private static List<string> TokensOf(string value) =>
        (value ?? string.Empty)
            .ToLowerInvariant()
            .Split([' ', '\t', '/', '-', '_', '.', '(', ')', '*', ':', '#', ',', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetter).ToArray()))
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>
    /// Decorative fill characters that some banks wrap around heading text (e.g.
    /// "*******Debit*******", "======Balance======"). They carry no meaning but greatly inflate a
    /// word's width, which shrinks the visible gap to the neighbouring heading and can cause two
    /// distinct columns (e.g. Debit and Credit) to be merged into one during grouping.
    /// </summary>
    private static readonly char[] DecorativeChars = ['*', '=', '_', '~'];

    /// <summary>
    /// Removes leading/trailing decorative wrapper characters (see <see cref="DecorativeChars"/>)
    /// from each header word and shrinks its bounding box proportionally so the true horizontal gaps
    /// between headings are restored. This prevents asterisk-decorated amount headers such as
    /// "*******Debit*******  *******Credit*******  ******Balance*******" from merging into a single
    /// column (which would otherwise be misclassified as one Balance column). Words without such
    /// decoration are returned unchanged.
    /// </summary>
    private static IReadOnlyList<PositionedWord> TrimDecorativeWords(IReadOnlyList<PositionedWord> words)
    {
        var result = new List<PositionedWord>(words.Count);
        foreach (PositionedWord word in words)
        {
            string text = word.Text;
            int lead = 0;
            while (lead < text.Length && Array.IndexOf(DecorativeChars, text[lead]) >= 0)
            {
                lead++;
            }

            int trail = 0;
            while (trail < text.Length - lead && Array.IndexOf(DecorativeChars, text[text.Length - 1 - trail]) >= 0)
            {
                trail++;
            }

            if (lead == 0 && trail == 0)
            {
                result.Add(word);
                continue;
            }

            string trimmed = text.Substring(lead, text.Length - lead - trail);
            if (trimmed.Length == 0)
            {
                // Purely decorative token (e.g. "........" or "*****"): drop it so it neither forms a
                // column nor influences the gap statistics.
                continue;
            }

            double width = Math.Abs(word.Right - word.Left);
            double perChar = text.Length > 0 ? width / text.Length : 0.0;
            double newLeft = word.Left + (lead * perChar);
            double newRight = word.Right - (trail * perChar);
            result.Add(new PositionedWord(trimmed, newLeft, newRight, word.Bottom, word.Top));
        }

        return result;
    }

    /// <summary>
    /// Splits a physical line into column groups. Words separated by a wide horizontal gap belong to
    /// different columns, while words separated by a normal space form one multi word heading.
    /// </summary>
    private static List<ColumnGroup> GroupIntoColumns(TextLine line)
    {
        var groups = new List<ColumnGroup>();
        IReadOnlyList<PositionedWord> words = TrimDecorativeWords(line.Words);
        if (words.Count == 0)
        {
            return groups;
        }

        double totalWidth = 0;
        int totalChars = 0;
        foreach (PositionedWord word in words)
        {
            totalWidth += Math.Abs(word.Right - word.Left);
            totalChars += Math.Max(word.Text.Length, 1);
        }

        double averageCharWidth = totalChars > 0 ? totalWidth / totalChars : 5.0;

        // Collect the horizontal gaps between consecutive words. Tightly-packed statement headers
        // (e.g. MCB: "Tran. Date  Effect Date  Tran. Br.  Transaction Details ...") have two clearly
        // separated populations of gaps: small intra-heading spaces (between "Tran." and "Date") and
        // larger inter-column gaps. A fixed multiple of the average character width is too coarse to
        // tell them apart, so we derive the column threshold from the gaps actually present on the
        // line: the smallest gap that is still clearly wider than the typical word-to-word space.
        var gaps = new List<double>();
        for (int i = 1; i < words.Count; i++)
        {
            double gap = words[i].Left - words[i - 1].Right;
            if (gap > 0)
            {
                gaps.Add(gap);
            }
        }

        double columnGap = averageCharWidth * 2.5;
        if (gaps.Count > 0)
        {
            gaps.Sort();
            double medianGap = gaps[gaps.Count / 2];

            // A column break is a gap noticeably larger than the median word spacing. Using the median
            // (rather than a fixed char-width multiple) adapts to tightly-packed headers while still
            // keeping genuine multi-word headings ("Transaction Details") together.
            double adaptiveGap = Math.Max(medianGap * 1.8, medianGap + averageCharWidth);

            // Never let the adaptive threshold exceed the coarse fixed one; only tighten it. This keeps
            // well-spaced statements behaving exactly as before while fixing tightly-packed ones.
            columnGap = Math.Min(columnGap, adaptiveGap);

            // Guard against a degenerate line where all gaps are essentially equal (single wide column):
            // fall back to the fixed threshold so we do not over-split a real multi-word heading.
            if (adaptiveGap <= medianGap + 0.5)
            {
                columnGap = averageCharWidth * 2.5;
            }
        }

        ColumnGroup? current = null;
        double previousRight = double.MinValue;

        foreach (PositionedWord word in words)
        {
            double left = word.Left;
            double right = word.Right;

            if (current is null || left - previousRight > columnGap)
            {
                current = new ColumnGroup(left);
                groups.Add(current);
            }

            current.Append(word.Text, right);
            previousRight = right;
        }

        return groups;
    }

    private static bool ContainsHeaderWord(string heading)
    {
        foreach (string token in Tokenize(heading))
        {
            if (HeaderWords.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> Tokenize(string heading) =>
        (heading ?? string.Empty)
            .ToLowerInvariant()
            .Split([' ', '\t', '/', '-', '_', '.', '(', ')', '*', ':', '#', ',', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            // Strip any remaining non-letter characters (e.g. OCR noise such as asterisks or brackets
            // that decorate scanned headings like "***Particulars******" or "Date(DD/MM)") so a heading
            // word is still recognised.
            .Select(t => new string(t.Where(char.IsLetter).ToArray()))
            .Where(t => t.Length > 0);

    private sealed class ColumnGroup(double left)
    {
        private readonly StringBuilder _text = new();

        public double Left { get; } = left;

        public double Right { get; private set; }

        public string Text => _text.ToString();

        public void Append(string word, double right)
        {
            if (_text.Length > 0)
            {
                _text.Append(' ');
            }

            _text.Append(word);
            Right = right;
        }
    }
}

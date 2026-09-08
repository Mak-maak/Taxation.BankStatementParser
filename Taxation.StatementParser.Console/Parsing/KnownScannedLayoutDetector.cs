using System.Text.RegularExpressions;
using Taxation.StatementParser.Console.Models;

namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// A resilient fallback header detector for fixed-format bank statements captured as low-quality
/// scans/photos, where OCR mangles the heading row so badly that the generic
/// <see cref="HeaderAutoDetector"/> (which relies on recognisable heading words) cannot match it.
///
/// These statements always share the same left-to-right column sequence:
///   Transaction Date | Description / Particulars | amount columns (Debit, Credit, Balance).
///
/// Instead of trusting the noisy heading text, this detector anchors the header GEOMETRICALLY from
/// the DATA rows, which OCR reproduces far more reliably than headings:
///   1. It finds the header line as the line that contains a surviving "date" token and sits directly
///      above a run of transaction rows that each begin with a <c>dd/MM/yy(yy)</c> date.
///   2. It derives the Transaction Date column from the X positions of those leading dates.
///   3. It derives the amount region from the X positions of the monetary values on the right, and
///      splits it into up to three bands (Debit, Credit, Balance) when they are clearly separated.
///   4. Everything horizontally between the date column and the amount region becomes the Description.
///
/// The result is expressed with the same <see cref="ColumnLayout"/> the rest of the pipeline uses, so
/// transaction assembly, multi-line description merging and Excel output all work unchanged.
/// </summary>
internal static class KnownScannedLayoutDetector
{
    /// <summary>A leading transaction date such as 02/07/25 or 02/07/2025 (day/month/year).</summary>
    private static readonly Regex LeadingDate =
        new(@"^\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}$", RegexOptions.Compiled);

    /// <summary>A monetary amount such as 4,250.00 or -91,000.00 or 2938.30 (2 decimal places).</summary>
    private static readonly Regex Amount =
        new(@"^-?\d{1,3}(,\d{3})*(\.\d{2})$|^-?\d+\.\d{2}$", RegexOptions.Compiled);

    /// <summary>A horizontal amount cluster (one amount column) with its extent and sign profile.</summary>
    private readonly record struct AmountBand(double Left, double Right, bool HasNegative)
    {
        public double Centre => (Left + Right) / 2.0;
    }


    public sealed record Result(
        IReadOnlyList<string> Columns,
        int AnchorColumnIndex,
        int HeaderLineIndex,
        ColumnLayout Layout);

    /// <summary>
    /// Attempts to detect the fixed scanned layout. Returns <c>null</c> when the page does not look
    /// like one of these statements (so the caller can fall back to the normal "header not found" path).
    /// </summary>
    public static Result? Detect(IReadOnlyList<TextLine> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (!LooksLikeHeaderLine(lines[i]))
            {
                continue;
            }

            // Collect the transaction data rows that begin with a date directly beneath this header.
            List<PositionedWord> leadingDates = CollectLeadingDates(lines, i);
            if (leadingDates.Count < 2)
            {
                continue;
            }

            List<PositionedWord> amounts = CollectAmounts(lines, i);
            if (amounts.Count < 2)
            {
                continue;
            }

            return Build(leadingDates, amounts, headerLineIndex: i);
        }

        return null;
    }

    /// <summary>
    /// A candidate header line contains a surviving "date" token (OCR keeps the word "Date" far more
    /// reliably than the rest of the heading) and is not itself a data row.
    /// </summary>
    private static bool LooksLikeHeaderLine(TextLine line)
    {
        if (line.Words.Count == 0)
        {
            return false;
        }

        bool hasDateWord = line.Words.Any(w => LettersOnly(w.Text).Equals("date", StringComparison.OrdinalIgnoreCase)
            || LettersOnly(w.Text).StartsWith("date", StringComparison.OrdinalIgnoreCase));

        // The header itself must not start with a real date value (that would be a data row).
        bool startsWithDate = LeadingDate.IsMatch(line.Words[0].Text.Trim());

        return hasDateWord && !startsWithDate;
    }

    private static List<PositionedWord> CollectLeadingDates(IReadOnlyList<TextLine> lines, int headerIndex)
    {
        var dates = new List<PositionedWord>();
        for (int i = headerIndex + 1; i < lines.Count; i++)
        {
            PositionedWord? first = lines[i].Words.Count > 0 ? lines[i].Words[0] : null;
            if (first is { } word && LeadingDate.IsMatch(word.Text.Trim()))
            {
                dates.Add(word);
            }
        }

        return dates;
    }

    private static List<PositionedWord> CollectAmounts(IReadOnlyList<TextLine> lines, int headerIndex)
    {
        var amounts = new List<PositionedWord>();
        for (int i = headerIndex + 1; i < lines.Count; i++)
        {
            foreach (PositionedWord word in lines[i].Words)
            {
                if (Amount.IsMatch(word.Text.Trim()))
                {
                    amounts.Add(word);
                }
            }
        }

        return amounts;
    }

    /// <summary>
    /// Builds the canonical column layout from the geometry of the data rows. The Transaction Date
    /// band comes from the leading dates; the amount region (right side) is split into up to three
    /// bands. The Debit column is identified by the negative sign that debit amounts carry (it is
    /// typically the first amount column); the remaining bands become Credit and Balance. The
    /// Description occupies the gap between the date column and the amount region.
    /// </summary>
    private static Result Build(
        List<PositionedWord> leadingDates,
        List<PositionedWord> amounts,
        int headerLineIndex)
    {
        double dateLeft = leadingDates.Min(w => w.Left);
        double dateRight = leadingDates.Max(w => w.Right);

        // Cluster amount X-centres into columns (a new cluster starts when the centres are separated
        // by more than a generous fraction of the typical amount width).
        double amountWidth = amounts.Average(w => Math.Abs(w.Right - w.Left));
        double clusterGap = Math.Max(amountWidth * 1.5, 20.0);

        List<AmountBand> amountBands = ClusterByCentre(amounts, clusterGap);

        // Keep the right-most bands (Debit, Credit, Balance) and cap at three so noise on the far
        // left does not masquerade as an amount column.
        amountBands = amountBands
            .OrderBy(b => b.Centre)
            .Where(b => b.Centre > dateRight)
            .ToList();

        if (amountBands.Count > 3)
        {
            amountBands = amountBands.TakeLast(3).ToList();
        }

        double amountRegionLeft = amountBands.Count > 0 ? amountBands.Min(b => b.Left) : double.MaxValue;

        // Description sits between the date column and the amount region.
        double descLeft = dateRight;
        double descRight = amountRegionLeft;

        // Assemble the output columns and their geometric extents in left-to-right order.
        var names = new List<string> { "Transaction Date", "Description" };
        var extents = new List<(double Left, double Right)>
        {
            (dateLeft, dateRight),
            (descLeft, descRight),
        };

        string[] amountNames = AmountColumnNames(amountBands);
        for (int b = 0; b < amountBands.Count; b++)
        {
            names.Add(amountNames[b]);
            extents.Add((amountBands[b].Left, amountBands[b].Right));
        }

        var layout = ColumnLayout.FromHeaderExtents(extents.ToArray());
        return new Result(names, AnchorColumnIndex: 0, headerLineIndex, layout);
    }

    /// <summary>
    /// Names the amount bands (ordered left-to-right) for the known statement sequence. Debit amounts
    /// carry a negative sign, so the first (left-most) band that contains a negative value is labelled
    /// Debit; the columns to its right become Credit and Balance. When no band shows a negative sign we
    /// fall back to the conventional left-to-right sequence, treating the first amount column as Debit.
    /// </summary>
    private static string[] AmountColumnNames(IReadOnlyList<AmountBand> bands)
    {
        int count = bands.Count;
        if (count == 0)
        {
            return [];
        }

        // These statements place their amount columns in the fixed left-to-right sequence
        // Debit | Credit | Balance. When exactly those three bands are present, honour that sequence
        // positionally: the sign heuristic is unreliable here because the Balance column can itself
        // carry a negative (overdrawn) sign, which would otherwise shift the labels and drop Balance.
        if (count == 3)
        {
            return ["Debit", "Credit", "Balance"];
        }

        // The first amount column is Debit: prefer the left-most band that actually carries a negative
        // (debit) value; otherwise assume the conventional first-column-is-debit layout.
        int debitIndex = -1;
        for (int i = 0; i < count; i++)
        {
            if (bands[i].HasNegative)
            {
                debitIndex = i;
                break;
            }
        }

        if (debitIndex < 0)
        {
            debitIndex = 0;
        }

        var names = new string[count];
        // Everything to the left of the debit column (rare) is treated as a generic Amount column.
        for (int i = 0; i < debitIndex; i++)
        {
            names[i] = "Amount";
        }

        // Debit, then Credit, then Balance for the columns from the debit column rightwards.
        string[] sequence = ["Debit", "Credit", "Balance"];
        for (int i = debitIndex, s = 0; i < count; i++, s++)
        {
            names[i] = s < sequence.Length ? sequence[s] : "Balance";
        }

        return names;
    }

    /// <summary>
    /// Groups words into horizontal clusters by their centre X, returning each cluster's extent and
    /// whether any value in it carries a negative sign (which marks a debit column). Words whose centres
    /// are within <paramref name="clusterGap"/> of the running cluster join it; a larger separation
    /// starts a new cluster.
    /// </summary>
    private static List<AmountBand> ClusterByCentre(
        IReadOnlyList<PositionedWord> words,
        double clusterGap)
    {
        var ordered = words
            .Select(w => (Centre: (w.Left + w.Right) / 2.0, w.Left, w.Right,
                Negative: w.Text.TrimStart().StartsWith('-')))
            .OrderBy(x => x.Centre)
            .ToList();

        var bands = new List<AmountBand>();
        double clusterLeft = 0, clusterRight = 0, lastCentre = double.MinValue;
        bool clusterNegative = false;
        bool open = false;

        foreach (var item in ordered)
        {
            if (!open || item.Centre - lastCentre > clusterGap)
            {
                if (open)
                {
                    bands.Add(new AmountBand(clusterLeft, clusterRight, clusterNegative));
                }

                clusterLeft = item.Left;
                clusterRight = item.Right;
                clusterNegative = item.Negative;
                open = true;
            }
            else
            {
                clusterLeft = Math.Min(clusterLeft, item.Left);
                clusterRight = Math.Max(clusterRight, item.Right);
                clusterNegative |= item.Negative;
            }

            lastCentre = item.Centre;
        }

        if (open)
        {
            bands.Add(new AmountBand(clusterLeft, clusterRight, clusterNegative));
        }

        return bands;
    }

    private static string LettersOnly(string value) =>
        new([.. (value ?? string.Empty).Where(char.IsLetter)]);
}

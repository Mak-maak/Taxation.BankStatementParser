namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// Horizontal (X axis) boundaries for each column, derived from the header row.
/// A word is assigned to the column whose [Min, Max) range contains the word's horizontal centre.
/// Boundaries between adjacent columns are the mid points of the whitespace gap that separates
/// their headings, which cleanly separates left aligned text and right aligned amounts.
/// </summary>
internal sealed class ColumnLayout
{
    private readonly double[] _min;
    private readonly double[] _max;

    // Maps each geometric layout column to its output column index, or -1 when that layout column is
    // an ignored (non-canonical) heading whose data must be discarded.
    private readonly int[] _layoutToOutput;

    private ColumnLayout(double[] min, double[] max, int[] layoutToOutput)
    {
        _min = min;
        _max = max;
        _layoutToOutput = layoutToOutput;
    }

    public int ColumnCount => _min.Length;

    public static ColumnLayout FromHeaderExtents((double Left, double Right)[] extents)
    {
        // Identity mapping: every column is a kept output column.
        int[] identity = Enumerable.Range(0, extents.Length).ToArray();
        return FromHeaderExtents(extents, identity);
    }

    /// <summary>
    /// Builds the layout from every detected header extent while mapping each geometric column to an
    /// output column. Columns not present in <paramref name="outputToLayout"/> are treated as ignored
    /// (non-canonical) and their assigned text is discarded, which keeps word placement accurate
    /// without emitting the extra column.
    /// </summary>
    public static ColumnLayout FromHeaderExtents(
        (double Left, double Right)[] extents,
        IReadOnlyList<int> outputToLayout)
    {
        int count = extents.Length;

        var layoutToOutput = new int[count];
        Array.Fill(layoutToOutput, -1);
        for (int output = 0; output < outputToLayout.Count; output++)
        {
            layoutToOutput[outputToLayout[output]] = output;
        }

        // Order columns by their physical left position while remembering their original index,
        // so the caller keeps the user supplied column order.
        int[] order = Enumerable.Range(0, count)
            .OrderBy(i => extents[i].Left)
            .ToArray();

        var min = new double[count];
        var max = new double[count];

        for (int position = 0; position < count; position++)
        {
            int columnIndex = order[position];

            double lowerBound = double.MinValue;
            if (position > 0)
            {
                int prevIndex = order[position - 1];
                lowerBound = (extents[prevIndex].Right + extents[columnIndex].Left) / 2.0;
            }

            double upperBound = double.MaxValue;
            if (position < count - 1)
            {
                int nextIndex = order[position + 1];
                upperBound = (extents[columnIndex].Right + extents[nextIndex].Left) / 2.0;
            }

            min[columnIndex] = lowerBound;
            max[columnIndex] = upperBound;
        }

        return new ColumnLayout(min, max, layoutToOutput);
    }

    /// <summary>
    /// Returns the OUTPUT column index for a word centred at <paramref name="centreX"/>, or -1 when the
    /// word falls under an ignored (non-canonical) heading and should be discarded.
    /// </summary>
    public int ColumnForX(double centreX) =>
        _layoutToOutput[LayoutColumnForX(centreX)];

    private int LayoutColumnForX(double centreX)
    {
        for (int c = 0; c < _min.Length; c++)
        {
            if (centreX >= _min[c] && centreX < _max[c])
            {
                return c;
            }
        }

        // Fallback: nearest column midpoint (defensive; boundaries already cover the full range).
        int nearest = 0;
        double best = double.MaxValue;
        for (int c = 0; c < _min.Length; c++)
        {
            double lo = _min[c] == double.MinValue ? _max[c] : _min[c];
            double hi = _max[c] == double.MaxValue ? _min[c] : _max[c];
            double mid = (lo + hi) / 2.0;
            double distance = Math.Abs(centreX - mid);
            if (distance < best)
            {
                best = distance;
                nearest = c;
            }
        }

        return nearest;
    }
}

using UglyToad.PdfPig.Content;

namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// A single word together with its bounding box, expressed in a bottom-left origin coordinate
/// space (larger <see cref="Top"/> means higher up the page). This is the geometric unit the whole
/// parsing pipeline operates on. It deliberately abstracts away the source of the word so the exact
/// same column/transaction detection works for both selectable-text PDFs (via PdfPig) and scanned
/// documents (via OCR).
/// </summary>
internal readonly struct PositionedWord
{
    public PositionedWord(string text, double left, double right, double bottom, double top)
    {
        Text = text;
        Left = left;
        Right = right;
        Bottom = bottom;
        Top = top;
    }

    public string Text { get; }

    public double Left { get; }

    public double Right { get; }

    /// <summary>Lower vertical edge (bottom-left origin: smaller value == lower on the page).</summary>
    public double Bottom { get; }

    /// <summary>Upper vertical edge (bottom-left origin: larger value == higher on the page).</summary>
    public double Top { get; }

    public double Height => Math.Abs(Top - Bottom);

    /// <summary>Adapts a PdfPig <see cref="Word"/> (already bottom-left origin) to a positioned word.</summary>
    public static PositionedWord FromPdfWord(Word word) => new(
        word.Text,
        word.BoundingBox.Left,
        word.BoundingBox.Right,
        word.BoundingBox.Bottom,
        word.BoundingBox.Top);
}

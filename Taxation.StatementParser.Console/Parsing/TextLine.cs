namespace Taxation.StatementParser.Console.Parsing;

/// <summary>
/// A cluster of words that share (approximately) the same vertical position on a page,
/// i.e. one physical line of text. Words are stored as <see cref="PositionedWord"/> so the line is
/// agnostic to whether they came from a text PDF or from OCR.
/// </summary>
internal sealed class TextLine
{
    private readonly List<PositionedWord> _words = [];

    public TextLine(double centreY) => CentreY = centreY;

    /// <summary>Vertical centre (bottom-left coordinate space) used to cluster words into this line.</summary>
    public double CentreY { get; }

    public IReadOnlyList<PositionedWord> Words => _words;

    /// <summary>The words of this line joined left-to-right into a single string.</summary>
    public string Text => string.Join(' ', _words.Select(w => w.Text));

    public void Add(PositionedWord word) => _words.Add(word);

    public void SortWordsLeftToRight() =>
        _words.Sort((a, b) => a.Left.CompareTo(b.Left));
}

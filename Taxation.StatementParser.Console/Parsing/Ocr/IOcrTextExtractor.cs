namespace Taxation.StatementParser.Console.Parsing.Ocr;

/// <summary>
/// Abstraction over the offline OCR engine that turns a raster image (a scanned statement page or a
/// captured photo) into a set of <see cref="PositionedWord"/>. Because the words carry bounding
/// boxes, the resulting output feeds the exact same geometric column/transaction pipeline used for
/// selectable-text PDFs — no separate parser is needed for scanned documents.
/// <para>
/// The abstraction keeps the OCR backend swappable: the parsing pipeline depends only on positioned
/// words and does not care whether they came from Tesseract, PaddleOCR, or any other engine.
/// </para>
/// </summary>
internal interface IOcrTextExtractor : IDisposable
{
    /// <summary>Recognizes the words in a PNG/JPEG/TIFF image supplied as raw bytes.</summary>
    IReadOnlyList<PositionedWord> ExtractFromImageBytes(byte[] imageBytes);

    /// <summary>Recognizes the words in an image file (PNG, JPG, JPEG, TIF, TIFF, BMP).</summary>
    IReadOnlyList<PositionedWord> ExtractFromImageFile(string imagePath);
}

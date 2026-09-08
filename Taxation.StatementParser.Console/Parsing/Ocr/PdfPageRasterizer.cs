using PDFtoImage;

namespace Taxation.StatementParser.Console.Parsing.Ocr;

/// <summary>
/// Renders a single PDF page to a raster image (PNG) so that scanned / image-only PDF pages can be
/// handed to the OCR engine. Text-based PDFs never reach this class; it is only used as a fallback
/// when a page yields no selectable text.
/// </summary>
internal static class PdfPageRasterizer
{
    /// <summary>
    /// A higher DPI produces sharper glyphs and materially better OCR accuracy at the cost of memory
    /// and time. 300 DPI is the widely recommended sweet spot for document OCR.
    /// </summary>
    private const int RenderDpi = 300;

    /// <summary>Renders the 1-based <paramref name="pageNumber"/> of the PDF to PNG image bytes.</summary>
    public static byte[] RenderPageToPng(string pdfPath, int pageNumber, string? password = null)
    {
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        byte[] pdfBytes = File.ReadAllBytes(pdfPath);

        using var stream = new MemoryStream();
        Conversion.SavePng(
            stream,
            pdfBytes,
            page: pageNumber - 1,
            password: password,
            options: new RenderOptions(Dpi: RenderDpi));

        return stream.ToArray();
    }
}

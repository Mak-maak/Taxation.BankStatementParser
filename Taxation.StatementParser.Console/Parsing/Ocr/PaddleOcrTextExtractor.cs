using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;

namespace Taxation.StatementParser.Console.Parsing.Ocr;

/// <summary>
/// Offline OCR engine backed by PaddleOCR (via the Sdcb.PaddleOCR .NET binding). PaddleOCR uses
/// deep-learning detection + recognition models and is dramatically more accurate than classic OCR
/// on low-quality scans and phone photos of bank statements — the misread dates/amounts, dropped
/// columns and footer text bleeding into amount cells that plagued the previous engine.
/// <para>
/// It runs fully offline: the detection/recognition/classification models are bundled in the
/// <c>Sdcb.PaddleOCR.Models.LocalV4</c> package and the inference runs on the local CPU through the
/// native runtime — no network access and no runtime model downloads.
/// </para>
/// <para>
/// Each recognized text region is converted into <see cref="PositionedWord"/>s in the bottom-left
/// origin coordinate space the parsing pipeline expects, so the existing geometric column/
/// transaction detection works unchanged.
/// </para>
/// </summary>
internal sealed class PaddleOcrTextExtractor : IOcrTextExtractor
{
    // Below this width a scan is too low-resolution for reliable recognition; upscale it first.
    private const int TargetMinWidth = 1600;

    private readonly object _sync = new();
    private PaddleOcrAll? _engine;

    /// <summary>Recognizes the words in a PNG/JPEG/TIFF image supplied as raw bytes.</summary>
    public IReadOnlyList<PositionedWord> ExtractFromImageBytes(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        using Mat source = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidOperationException("The supplied image could not be decoded for OCR.");
        }

        return Recognize(source);
    }

    /// <summary>Recognizes the words in an image file (PNG, JPG, JPEG, TIF, TIFF, BMP).</summary>
    public IReadOnlyList<PositionedWord> ExtractFromImageFile(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            throw new FileNotFoundException("The specified image file was not found.", imagePath);
        }

        using Mat source = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidOperationException($"The image '{imagePath}' could not be decoded for OCR.");
        }

        return Recognize(source);
    }

    private IReadOnlyList<PositionedWord> Recognize(Mat source)
    {
        using Mat prepared = Preprocess(source);

        // Some scans/photos are stored upside-down (180° rotated). The offline model set does not
        // include the 180° classifier, so correct orientation explicitly: OCR the image as-is and
        // rotated 180°, then keep whichever orientation reads more like a real statement. Flipped Latin
        // glyphs are often still recognized as letters, so ASCII ratio alone is not decisive; the number
        // of recognizable date tokens (every statement has several) is a reliable orientation signal.
        PaddleOcrResult upright = RunOcr(prepared);

        using Mat flippedImage = new();
        Cv2.Rotate(prepared, flippedImage, RotateFlags.Rotate180);
        PaddleOcrResult flippedResult = RunOcr(flippedImage);

        bool useFlipped = OrientationScore(flippedResult) > OrientationScore(upright);
        PaddleOcrResult chosen = useFlipped ? flippedResult : upright;
        Mat chosenImage = useFlipped ? flippedImage : prepared;

        int imageHeight = chosenImage.Height;
        var words = new List<PositionedWord>();

        foreach (PaddleOcrResultRegion region in chosen.Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Text))
            {
                continue;
            }

            // PaddleOCR returns a rotated rectangle per detected text line. Reduce it to an
            // axis-aligned bounding box (only relative positions matter to the pipeline).
            Rect box = region.Rect.BoundingRect();
            AppendRegionWords(words, region.Text.Trim(), box, imageHeight);
        }

        return words;
    }

    private PaddleOcrResult RunOcr(Mat image)
    {
        lock (_sync)
        {
            return GetEngine().Run(image);
        }
    }

    /// <summary>
    /// Scores how much a recognition result looks like a real bank statement, used to pick the correct
    /// page orientation. Every statement contains several date tokens (dd/mm/yy or dd-mm-yyyy); an
    /// upside-down page recognizes almost none, so the count of date-like tokens is a robust signal,
    /// combined with the ratio of Latin/digit characters as a tie-breaker.
    /// </summary>
    private static double OrientationScore(PaddleOcrResult result)
    {
        int dateTokens = 0;
        int letters = 0;
        int total = 0;

        foreach (PaddleOcrResultRegion region in result.Regions)
        {
            string text = region.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(
                    text, @"\b\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}\b"))
            {
                dateTokens++;
            }

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                total++;
                if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                {
                    letters++;
                }
            }
        }

        double asciiRatio = total == 0 ? 0 : (double)letters / total;

        // Weight date tokens heavily (they are decisive) and use the ASCII ratio as a fine tie-breaker.
        return (dateTokens * 10.0) + asciiRatio;
    }

    /// <summary>
    /// PaddleOCR groups a whole text line into one region. The parsing pipeline is word-oriented and
    /// assigns each word to a column by its horizontal centre, so a single wide region spanning
    /// multiple columns would be mis-assigned. Split the region text on whitespace and distribute the
    /// region's horizontal span across the words proportionally to their character length. This keeps
    /// column assignment accurate while preserving the recognized text.
    /// </summary>
    private static void AppendRegionWords(List<PositionedWord> words, string text, Rect box, int imageHeight)
    {
        // Tesseract used a top-left origin (Y grows downward). The pipeline expects a bottom-left
        // origin (larger Top == higher on the page), so flip Y against the image height.
        double top = imageHeight - box.Top;
        double bottom = imageHeight - box.Bottom;

        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1)
        {
            words.Add(new PositionedWord(text, box.Left, box.Right, bottom, top));
            return;
        }

        int totalChars = tokens.Sum(t => t.Length) + (tokens.Length - 1); // include single spaces
        double width = box.Width;
        double cursor = box.Left;

        foreach (string token in tokens)
        {
            double slice = width * ((token.Length + 1.0) / totalChars);
            double left = cursor;
            double right = cursor + width * ((double)token.Length / totalChars);
            words.Add(new PositionedWord(token, left, right, bottom, top));
            cursor += slice;
        }
    }

    /// <summary>
    /// Upscales small/low-resolution images so text is large enough for reliable recognition.
    /// PaddleOCR handles skew and contrast internally, so only sizing is normalized here. Defensive:
    /// if scaling fails for any reason the original image is used.
    /// </summary>
    private static Mat Preprocess(Mat source)
    {
        try
        {
            if (source.Width > 0 && source.Width < TargetMinWidth)
            {
                double scale = Math.Min(3.0, (double)TargetMinWidth / source.Width);
                if (scale > 1.05)
                {
                    var resized = new Mat();
                    Cv2.Resize(source, resized, new Size(0, 0), scale, scale, InterpolationFlags.Cubic);
                    return resized;
                }
            }
        }
        catch
        {
            // Fall through and use a clone of the original image.
        }

        return source.Clone();
    }

    private PaddleOcrAll GetEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        try
        {
            // PP-OCRv5's model ships its recognition weights embedded for fully-offline use and, despite
            // the name, recognizes Latin letters and digits well — the script needed for the English
            // bank statements this tool targets. (The V5 English/Latin entries are not embedded in the
            // local package, so they cannot be used offline.)
            FullOcrModel model = LocalFullModels.ChineseV5;
            _engine = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
            {
                // Bank statements are flat, axis-aligned tables. Disabling per-region rotation detection
                // improves accuracy on 0° text (per PaddleOCR guidance) and avoids the mixed/mirrored
                // output it produced here. Whole-page 180° (upside-down) scans are handled explicitly in
                // Recognize by OCR-ing both orientations and keeping the more statement-like one.
                AllowRotateDetection = false,
                Enable180Classification = false,
            };
            return _engine;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to initialise the offline PaddleOCR engine. Ensure the PaddleOCR model and " +
                "native runtime packages are installed and restored (Sdcb.PaddleOCR, " +
                "Sdcb.PaddleInference and the win64 runtime).",
                ex);
        }
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
    }
}

using FluentAssertions;
using Taxation.StatementParser.Console.Parsing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class KnownScannedLayoutDetectorTests
{
    // Column X-bands mirroring the fixed Meezan-style scanned statement:
    // Transaction Date | Description/Particulars | Debit | Credit | Balance.
    private const double DateX = 50;
    private const double DescX = 200;
    private const double DebitX = 500;
    private const double CreditX = 700;
    private const double BalanceX = 900;

    [Fact]
    public void Detect_WithGarbledHeader_ButCleanDataRows_ShouldAnchorGeometrically()
    {
        // The heading row is heavily OCR-corrupted (only "Date" survives), which defeats the word-based
        // HeaderAutoDetector. The data rows, however, are clean and are what the fallback anchors on.
        var lines = new List<TextLine>
        {
            Line(900, (DateX, "Date(D0/KN)"), (DescX, "Walbre"), (DebitX, "T==pee")),
            Line(870, (DateX, "02/07/25"), (DescX, "RAAST PYMT"), (CreditX, "4,250.00")),
            Line(840, (DateX, "02/07/25"), (DescX, "Cash Withdrawal"), (DebitX, "-91,000.00"), (BalanceX, "2,938.30")),
            Line(810, (DateX, "03/07/25"), (DescX, "Internet Funds Transfer"), (CreditX, "3,600.00")),
            Line(780, (DateX, "03/07/25"), (DescX, "Bank Charges"), (DebitX, "-35.00"), (BalanceX, "6,503.30")),
        };

        KnownScannedLayoutDetector.Result? result = KnownScannedLayoutDetector.Detect(lines);

        result.Should().NotBeNull();
        result!.Columns.Should().Contain("Transaction Date");
        result.Columns.Should().Contain("Description");
        result.Columns[result.AnchorColumnIndex].Should().Be("Transaction Date");
        result.HeaderLineIndex.Should().Be(0);
    }

    [Fact]
    public void Detect_ShouldLabelFirstNegativeAmountColumnAsDebit()
    {
        // Debit amounts carry a negative sign. The left-most amount column that contains a negative
        // value must be labelled Debit, with the following columns becoming Credit and Balance.
        var lines = new List<TextLine>
        {
            Line(900, (DateX, "Date(D0/KN)"), (DescX, "Walbre")),
            Line(870, (DateX, "02/07/25"), (DescX, "Cash Withdrawal"), (DebitX, "-91,000.00"), (CreditX, "4,250.00"), (BalanceX, "2,938.30")),
            Line(840, (DateX, "03/07/25"), (DescX, "Bank Charges"), (DebitX, "-35.00"), (BalanceX, "6,503.30")),
            Line(810, (DateX, "03/07/25"), (DescX, "Funds Transfer"), (CreditX, "3,600.00"), (BalanceX, "10,103.30")),
        };

        KnownScannedLayoutDetector.Result? result = KnownScannedLayoutDetector.Detect(lines);

        result.Should().NotBeNull();
        result!.Columns.Should().ContainInOrder("Debit", "Credit", "Balance");
        var columns = result.Columns.ToList();
        columns.IndexOf("Debit").Should().BeLessThan(columns.IndexOf("Credit"));
    }

    [Fact]
    public void Detect_WithThreeAmountColumns_ShouldLabelThemDebitCreditBalancePositionally()
    {
        // The fixed statement layout is Debit | Credit | Balance. Even when the Balance column carries
        // a negative (overdrawn) sign in the left-most-negative position, all three amount columns must
        // still be labelled positionally so the final Balance column is never dropped.
        var lines = new List<TextLine>
        {
            Line(900, (DateX, "Date(D0/KN)"), (DescX, "Walbre")),
            Line(870, (DateX, "02/07/25"), (DescX, "Cash Withdrawal"), (DebitX, "91,000.00"), (CreditX, "4,250.00"), (BalanceX, "-2,938.30")),
            Line(840, (DateX, "03/07/25"), (DescX, "Bank Charges"), (DebitX, "35.00"), (CreditX, "0.00"), (BalanceX, "-6,503.30")),
            Line(810, (DateX, "03/07/25"), (DescX, "Funds Transfer"), (DebitX, "0.00"), (CreditX, "3,600.00"), (BalanceX, "10,103.30")),
        };

        KnownScannedLayoutDetector.Result? result = KnownScannedLayoutDetector.Detect(lines);

        result.Should().NotBeNull();
        result!.Columns.Should().ContainInOrder("Debit", "Credit", "Balance");
    }

    [Fact]
    public void Detect_WithNoDataRows_ShouldReturnNull()
    {
        var lines = new List<TextLine>
        {
            Line(900, (DateX, "Date(D0/KN)"), (DescX, "Walbre")),
            Line(870, (DateX, "some"), (DescX, "free text only")),
        };

        KnownScannedLayoutDetector.Detect(lines).Should().BeNull();
    }

    [Fact]
    public void Detect_WithoutDateHeadingWord_ShouldReturnNull()
    {
        var lines = new List<TextLine>
        {
            Line(900, (DateX, "Foo"), (DescX, "Bar")),
            Line(870, (DateX, "02/07/25"), (DescX, "Payment"), (CreditX, "4,250.00")),
            Line(840, (DateX, "03/07/25"), (DescX, "Payment"), (CreditX, "3,600.00")),
        };

        KnownScannedLayoutDetector.Detect(lines).Should().BeNull();
    }

    private static TextLine Line(double centreY, params (double X, string Text)[] cells)
    {
        var line = new TextLine(centreY);
        foreach ((double x, string text) in cells)
        {
            // Approximate a word width of ~8 units per character for the bounding box.
            double right = x + Math.Max(text.Length * 8.0, 8.0);
            line.Add(new PositionedWord(text, x, right, centreY - 5, centreY + 5));
        }

        line.SortWordsLeftToRight();
        return line;
    }
}

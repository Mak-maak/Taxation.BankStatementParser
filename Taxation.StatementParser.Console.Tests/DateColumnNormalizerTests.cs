using FluentAssertions;
using Taxation.StatementParser.Console.Parsing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class DateColumnNormalizerTests
{
    [Theory]
    [InlineData("02/07/2025", "02/07/2025")]
    [InlineData("2/7/2025", "02/07/2025")]
    [InlineData("02-07-2025", "02/07/2025")]
    [InlineData("02.07.2025", "02/07/2025")]
    [InlineData("03 Jul 2025", "03/07/2025")]
    [InlineData("01 Aug 2025", "01/08/2025")]
    [InlineData("26 Oct 2025", "26/10/2025")]
    [InlineData("02-JUN-25", "02/06/2025")]
    [InlineData("02-Jun-2025", "02/06/2025")]
    [InlineData("2-Jun-25", "02/06/2025")]
    [InlineData("15-DEC-24", "15/12/2024")]
    [InlineData("02/JUN/2025", "02/06/2025")]
    [InlineData("02.06.25", "02/06/2025")]
    [InlineData("1/7/25", "01/07/2025")]
    [InlineData("Jul 03, 2025", "03/07/2025")]
    [InlineData("July 3 2025", "03/07/2025")]
    public void TryNormalize_WithValidDate_NormalizesToCanonicalFormat(string input, string expected)
    {
        bool ok = DateColumnNormalizer.TryNormalize(input, out string date, out string noise);

        ok.Should().BeTrue();
        date.Should().Be(expected);
        noise.Should().BeEmpty();
    }

    [Theory]
    [InlineData("03 Jul 2025 Money", "03/07/2025", "Money")]
    [InlineData("26 Jul 2025 POS", "26/07/2025", "POS")]
    [InlineData("06 Aug 2025 1-Link", "06/08/2025", "1-Link")]
    [InlineData("27 Jul 2025 Raast", "27/07/2025", "Raast")]
    [InlineData("02-JUN-25 MONTHLY BUNDLE", "02/06/2025", "MONTHLY BUNDLE")]
    public void TryNormalize_WhenDescriptionBledIntoDate_SeparatesTrailingNoise(
        string input, string expectedDate, string expectedNoise)
    {
        bool ok = DateColumnNormalizer.TryNormalize(input, out string date, out string noise);

        ok.Should().BeTrue();
        date.Should().Be(expectedDate);
        noise.Should().Be(expectedNoise);
    }

    [Theory]
    [InlineData("1 22 Aug 2026, 16:51")]   // page footer: page number + print stamp
    [InlineData("7 22 Aug 2026, 16:51")]
    [InlineData("22 Aug 2026, 16:51")]      // bare print stamp (has a time component)
    public void TryNormalize_WithFooterTimestamp_IsRejected(string input)
    {
        DateColumnNormalizer.TryNormalize(input, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("ANWAR")]
    [InlineData("HBL")]
    [InlineData("HILL")]
    [InlineData("to")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryNormalize_WithStrayNonDateText_IsRejected(string? input)
    {
        DateColumnNormalizer.TryNormalize(input, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void IsTransactionDate_MatchesTryNormalizeOutcome()
    {
        DateColumnNormalizer.IsTransactionDate("02/07/2025").Should().BeTrue();
        DateColumnNormalizer.IsTransactionDate("1 22 Aug 2026, 16:51").Should().BeFalse();
    }
}

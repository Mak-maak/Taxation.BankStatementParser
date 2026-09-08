using FluentAssertions;
using Taxation.StatementParser.Console.Parsing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class PositionedWordTests
{
    [Fact]
    public void Constructor_ShouldExposeSuppliedGeometry()
    {
        var word = new PositionedWord("Balance", left: 10, right: 60, bottom: 100, top: 118);

        word.Text.Should().Be("Balance");
        word.Left.Should().Be(10);
        word.Right.Should().Be(60);
        word.Bottom.Should().Be(100);
        word.Top.Should().Be(118);
    }

    [Fact]
    public void Height_ShouldBeAbsoluteDifferenceOfTopAndBottom()
    {
        var word = new PositionedWord("x", left: 0, right: 5, bottom: 100, top: 118);

        word.Height.Should().Be(18);
    }

    [Fact]
    public void Height_ShouldBeNonNegative_WhenBottomGreaterThanTop()
    {
        // Defensive: even if the coordinate space is supplied inverted, height stays positive.
        var word = new PositionedWord("x", left: 0, right: 5, bottom: 118, top: 100);

        word.Height.Should().Be(18);
    }
}

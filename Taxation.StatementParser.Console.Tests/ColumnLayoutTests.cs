using FluentAssertions;
using Taxation.StatementParser.Console.Parsing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class ColumnLayoutTests
{
    [Fact]
    public void FromHeaderExtents_ShouldAssignWordsToCorrectColumns()
    {
        // Three columns positioned left-to-right with clear gaps.
        (double Left, double Right)[] extents =
        [
            (10, 40),   // column 0
            (100, 160), // column 1
            (220, 260)  // column 2
        ];

        ColumnLayout layout = ColumnLayout.FromHeaderExtents(extents);

        layout.ColumnCount.Should().Be(3);
        layout.ColumnForX(20).Should().Be(0);
        layout.ColumnForX(130).Should().Be(1);
        layout.ColumnForX(240).Should().Be(2);
    }

    [Fact]
    public void FromHeaderExtents_ShouldPreserveOriginalOrder_WhenExtentsAreUnordered()
    {
        // Columns supplied out of physical order; layout must still map by original index.
        (double Left, double Right)[] extents =
        [
            (220, 260), // column 0 is physically rightmost
            (10, 40),   // column 1 is physically leftmost
            (100, 160)  // column 2 is in the middle
        ];

        ColumnLayout layout = ColumnLayout.FromHeaderExtents(extents);

        layout.ColumnForX(240).Should().Be(0);
        layout.ColumnForX(20).Should().Be(1);
        layout.ColumnForX(130).Should().Be(2);
    }

    [Fact]
    public void ColumnForX_ForExtremeValues_ShouldMapToEdgeColumns()
    {
        (double Left, double Right)[] extents = [(10, 40), (100, 160)];

        ColumnLayout layout = ColumnLayout.FromHeaderExtents(extents);

        layout.ColumnForX(-1000).Should().Be(0);
        layout.ColumnForX(100000).Should().Be(1);
    }
}

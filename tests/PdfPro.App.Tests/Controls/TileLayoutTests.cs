using System.Windows;
using PdfPro.App.Controls;

namespace PdfPro.App.Tests.Controls;

public sealed class TileLayoutTests
{
    [Fact]
    public void Tiles_FlowLeftToRightThenDown_CenteredInTheWidth()
    {
        var layout = new TileLayout(11, 200, 300, 1000);

        layout.Columns.ShouldBe(5);
        layout.Rows.ShouldBe(3);
        layout.Left.ShouldBe(0);
        layout.Height.ShouldBe(900);
        layout.TileRect(0).ShouldBe(new Rect(0, 0, 200, 300));
        layout.TileRect(6).ShouldBe(new Rect(200, 300, 200, 300));
        layout.RowOf(10).ShouldBe(2);

        var centered = new TileLayout(11, 200, 300, 1100);
        centered.Columns.ShouldBe(5);
        centered.Left.ShouldBe(50);
        centered.TileRect(0).X.ShouldBe(50);
    }

    [Fact]
    public void ANarrowView_StillHasOneColumn()
    {
        var layout = new TileLayout(3, 200, 300, 120);

        layout.Columns.ShouldBe(1);
        layout.Rows.ShouldBe(3);
        layout.Width.ShouldBe(200);
        layout.Left.ShouldBe(0);
    }

    [Fact]
    public void AnUnknownWidth_UsesOneTileWidth()
        => new TileLayout(4, 200, 300, double.PositiveInfinity).Columns.ShouldBe(1);

    [Fact]
    public void ATileSizeThatIsNotPositive_IsRefused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new TileLayout(1, 0, 300, 1000));
        Should.Throw<ArgumentOutOfRangeException>(() => new TileLayout(1, 200, double.NaN, 1000));
    }

    [Theory]
    [InlineData(0, 600, 0, 0, 9)] // rows 0-1
    [InlineData(0, 600, 1, 0, 14)] // plus one row below
    [InlineData(450, 300, 0, 5, 14)] // rows 1-2
    [InlineData(450, 300, 1, 0, 19)]
    [InlineData(5000, 300, 1, 20, 22)] // past the end: the last row
    public void RangeFor_GivesTheTilesOfTheRowsInView(double top, double height, int extraRows, int first, int last)
        => new TileLayout(23, 200, 300, 1000).RangeFor(top, height, extraRows).ShouldBe((first, last));

    [Fact]
    public void RangeFor_NoTiles_IsEmpty()
    {
        var (first, last) = new TileLayout(0, 200, 300, 1000).RangeFor(0, 600, 1);

        last.ShouldBeLessThan(first);
    }

    [Fact]
    public void IndicesIn_GivesTheTilesARectangleTouches()
    {
        var layout = new TileLayout(12, 200, 300, 1000); // 5 columns: rows 0-4, 5-9, 10-11

        layout.IndicesIn(new Rect(new Point(250, 100), new Point(450, 350))).ShouldBe([1, 2, 6, 7]);
        layout.IndicesIn(new Rect(new Point(990, 890), new Point(150, 350))).ShouldBe([5, 6, 7, 8, 9, 10, 11]);
        layout.IndicesIn(new Rect(10, 610, 5, 5)).ShouldBe([10]);
    }

    [Fact]
    public void IndicesIn_EmptyPartsOfTheGrid_TouchNothing()
    {
        var layout = new TileLayout(12, 200, 300, 1000);

        layout.IndicesIn(new Rect(600, 650, 300, 200)).ShouldBeEmpty(); // right of the last tile
        layout.IndicesIn(Rect.Empty).ShouldBeEmpty();
        layout.IndicesIn(new Rect(100, 100, 0, 50)).ShouldBeEmpty();
    }

    [Fact]
    public void IndicesIn_WithCenteredTiles_IgnoresTheMargin()
    {
        var layout = new TileLayout(5, 200, 300, 1100); // 50 on each side

        layout.IndicesIn(new Rect(0, 0, 45, 300)).ShouldBeEmpty();
        layout.IndicesIn(new Rect(0, 0, 60, 300)).ShouldBe([0]);
    }

    [Theory]
    [InlineData(7, 0, 600, 0)] // row 1 is in view: no move
    [InlineData(17, 0, 600, 600)] // row 3 ends at 1200: scroll down just enough
    [InlineData(2, 900, 600, 0)] // above the view: its top
    public void OffsetToShow_MovesAsLittleAsPossible(int index, double offset, double viewport, double expected)
    {
        var layout = new TileLayout(30, 200, 300, 1000);

        layout.OffsetToShow(index, offset, viewport).ShouldBe(expected);
    }

    [Fact]
    public void OffsetToShow_TileTallerThanTheView_ShowsItsTop()
        => new TileLayout(30, 200, 300, 1000).OffsetToShow(12, 0, 200).ShouldBe(600);
}

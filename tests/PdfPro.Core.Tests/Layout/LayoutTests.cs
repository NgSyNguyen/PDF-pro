using PdfPro.Core.Geometry;
using PdfPro.Core.Layout;

namespace PdfPro.Core.Tests.Layout;

public sealed class LayoutTests
{
    private static readonly PdfSize A4 = new(595.28, 841.89);
    private static readonly PdfSize A4Landscape = new(841.89, 595.28);

    [Theory]
    [InlineData(2, 2, 1, true)]
    [InlineData(4, 2, 2, false)]
    [InlineData(6, 3, 2, true)]
    [InlineData(8, 4, 2, true)]
    [InlineData(9, 3, 3, false)]
    [InlineData(16, 4, 4, false)]
    public void ChooseGrid_Auto_ShowsPortraitPagesLargest(int count, int columns, int rows, bool landscape)
    {
        var grid = NUpLayout.ChooseGrid(Options(count), A4);

        grid.Sheet.IsLandscape.ShouldBe(landscape);
        (grid.Columns, grid.Rows).ShouldBe(landscape ? (columns, rows) : (Math.Min(columns, rows), Math.Max(columns, rows)));
        grid.CellsPerSheet.ShouldBe(count);
    }

    [Fact]
    public void ChooseGrid_Auto_PutsTwoLandscapePagesAboveEachOther()
    {
        var grid = NUpLayout.ChooseGrid(Options(2), A4Landscape);

        grid.Sheet.IsLandscape.ShouldBeFalse();
        (grid.Columns, grid.Rows).ShouldBe((1, 2));
    }

    [Fact]
    public void ChooseGrid_HonoursAForcedOrientation()
    {
        var grid = NUpLayout.ChooseGrid(Options(6) with { Orientation = SheetOrientation.Portrait }, A4);

        grid.Sheet.ShouldBe(new PdfSize(595.28, 841.89));
        (grid.Columns, grid.Rows).ShouldBe((2, 3));
    }

    [Fact]
    public void ChooseGrid_RejectsBadOptions()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => NUpLayout.ChooseGrid(Options(3), A4));
        Should.Throw<ArgumentOutOfRangeException>(() => NUpLayout.ChooseGrid(Options(4) with { Margin = -1 }, A4));
        Should.Throw<ArgumentOutOfRangeException>(() => NUpLayout.ChooseGrid(Options(16) with { Margin = 297 }, A4));
        Should.Throw<ArgumentOutOfRangeException>(() => NUpLayout.ChooseGrid(Options(4), new PdfSize(0, 10)));
    }

    [Fact]
    public void Cells_FillRowsFirstOrColumnsFirst()
    {
        var z = NUpLayout.ChooseGrid(Options(4, margin: 10, gap: 10) with { Orientation = SheetOrientation.Portrait }, A4);
        var n = z with { Order = NUpOrder.ColumnsFirst };

        // Z: top-left, top-right, bottom-left, bottom-right.
        ShouldStartAt(z.Cell(0), 10, 841.89 - 10);
        ShouldStartAt(z.Cell(1), 10 + z.CellWidth + 10, 841.89 - 10);
        ShouldStartAt(z.Cell(2), 10, 841.89 - 10 - z.CellHeight - 10);
        ShouldStartAt(n.Cell(1), 10, 841.89 - 10 - z.CellHeight - 10);
        ShouldStartAt(n.Cell(2), 10 + z.CellWidth + 10, 841.89 - 10);
        z.Cell(3).Bottom.ShouldBe(10, 1e-9);
        z.Cell(3).Right.ShouldBe(595.28 - 10, 1e-9);
        Should.Throw<ArgumentOutOfRangeException>(() => z.Cell(4));
    }

    [Fact]
    public void Place_FitsAndCentresThePage()
    {
        var placed = NUpLayout.Place(new PdfRect(0, 0, 200, 100), new PdfSize(100, 100));

        placed.ShouldBe(new PdfRect(50, 0, 150, 100));
        NUpLayout.SheetCount(9, 4).ShouldBe(3);
        NUpLayout.SheetCount(0, 4).ShouldBe(0);
    }

    [Fact]
    public void Booklet_TenPages_PrintsTwelveInFoldOrder()
    {
        BookletLayout.PaddedPageCount(10).ShouldBe(12);

        var sides = BookletLayout.Sides(10);

        // 1-based: [12|1] [2|11] [10|3] [4|9] [8|5] [6|7]; pages 11 and 12 are blank.
        sides.ShouldBe([new(-1, 0), new(1, -1), new(9, 2), new(3, 8), new(7, 4), new(5, 6)]);
    }

    [Fact]
    public void Booklet_EveryPageAppearsOnceAndFacingPagesSumUp()
    {
        foreach (var count in new[] { 1, 4, 5, 8, 17 })
        {
            var sides = BookletLayout.Sides(count);
            var pages = sides.SelectMany(s => new[] { s.Left, s.Right }).Where(p => p >= 0).Order();

            pages.ShouldBe(Enumerable.Range(0, count));
            sides.Count.ShouldBe(BookletLayout.PaddedPageCount(count) / 2);
        }
    }

    [Fact]
    public void Booklet_RightBinding_MirrorsEachSide()
        => BookletLayout.Sides(4, BookletBinding.Right).ShouldBe([new(0, 3), new(2, 1)]);

    [Fact]
    public void Poster_A3OnA4_TwoSheetsWithOverlap()
    {
        var a3 = new PdfSize(841.89, 1190.55);
        var options = new PosterOptions(A4Landscape, Margin: 20, Overlap: 30, Scale: 1);

        var tiles = PosterLayout.Tiles(a3, options);

        // Printable 801.89 × 555.28, step 771.89 × 525.28: 2 columns, 3 rows.
        PosterLayout.Grid(a3, options).ShouldBe((2, 3));
        tiles.Count.ShouldBe(6);
        ShouldBeNear(tiles[0].Region, new PdfRect(0, 1190.55 - 555.28, 801.89, 1190.55));
        tiles[1].Region.Left.ShouldBe(771.89, 1e-9);
        tiles[1].Region.Right.ShouldBe(841.89, 1e-9);
        tiles[5].Region.Bottom.ShouldBe(0);
        ShouldBeNear(tiles[0].Target, new PdfRect(20, 20, 20 + 801.89, 595.28 - 20));
        tiles.ShouldAllBe(t => Math.Abs(t.Target.Width - t.Region.Width) < 1e-9 && Math.Abs(t.Target.Top - (595.28 - 20)) < 1e-9);
    }

    [Fact]
    public void Poster_PageThatFits_IsOneSheet()
        => PosterLayout.Grid(A4, new PosterOptions(A4, 10, 10, 0.5)).ShouldBe((1, 1));

    [Fact]
    public void Poster_ScaleToFit_UsesTheWholeGrid()
    {
        var a1 = new PdfSize(1683.78, 2383.94);

        var scale = PosterLayout.ScaleToFit(a1, A4, margin: 20, overlap: 20, columns: 2, rows: 2);

        PosterLayout.Grid(a1, new PosterOptions(A4, 20, 20, scale)).ShouldBe((2, 2));
        PosterLayout.Grid(a1, new PosterOptions(A4, 20, 20, scale * 1.01)).ShouldNotBe((2, 2));
    }

    [Fact]
    public void Poster_ChooseSheet_PicksTheOrientationNeedingFewerSheets()
    {
        var wide = new PdfSize(1500, 500);

        PosterLayout.ChooseSheet(wide, A4, 20, 20, 1).IsLandscape.ShouldBeTrue();
        PosterLayout.ChooseSheet(A4, A4, 20, 20, 0.5).IsLandscape.ShouldBeFalse();
    }

    [Fact]
    public void Poster_RejectsOverlapsAndMarginsThatLeaveNothing()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PosterLayout.Grid(A4, new PosterOptions(A4, 300, 0, 1)));
        Should.Throw<ArgumentOutOfRangeException>(() => PosterLayout.Grid(A4, new PosterOptions(A4, 20, 300, 1)));
        Should.Throw<ArgumentOutOfRangeException>(() => PosterLayout.Grid(A4, new PosterOptions(A4, 20, 20, 0)));
    }

    [Fact]
    public void Interleave_MergesFrontsAndBacks()
    {
        InterleaveOrder.Order(3, 3, reverseBacks: false).ShouldBe(
            [new(false, 0), new(true, 0), new(false, 1), new(true, 1), new(false, 2), new(true, 2)]);
        InterleaveOrder.Order(3, 3, reverseBacks: true).ShouldBe(
            [new(false, 0), new(true, 2), new(false, 1), new(true, 1), new(false, 2), new(true, 0)]);
    }

    [Fact]
    public void Interleave_PutsUnpairedPagesAtTheEnd()
    {
        InterleaveOrder.Order(3, 2, reverseBacks: true).ShouldBe(
            [new(false, 0), new(true, 1), new(false, 1), new(true, 0), new(false, 2)]);
        InterleaveOrder.Order(1, 3, reverseBacks: false).ShouldBe([new(false, 0), new(true, 0), new(true, 1), new(true, 2)]);
        InterleaveOrder.IsBalanced(3, 3).ShouldBeTrue();
        InterleaveOrder.IsBalanced(3, 2).ShouldBeTrue();
        InterleaveOrder.IsBalanced(3, 1).ShouldBeFalse();
        InterleaveOrder.IsBalanced(2, 3).ShouldBeFalse();
    }

    private static NUpOptions Options(int count, double margin = 0, double gap = 0)
        => new(count, A4, SheetOrientation.Auto, NUpOrder.RowsFirst, margin, gap);

    private static void ShouldStartAt(PdfRect rect, double left, double top)
    {
        rect.Left.ShouldBe(left, 1e-9);
        rect.Top.ShouldBe(top, 1e-9);
    }

    private static void ShouldBeNear(PdfRect actual, PdfRect expected)
    {
        actual.Left.ShouldBe(expected.Left, 1e-9);
        actual.Bottom.ShouldBe(expected.Bottom, 1e-9);
        actual.Right.ShouldBe(expected.Right, 1e-9);
        actual.Top.ShouldBe(expected.Top, 1e-9);
    }
}

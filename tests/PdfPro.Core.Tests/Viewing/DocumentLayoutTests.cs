using PdfPro.Core.Geometry;
using PdfPro.Core.Viewing;

namespace PdfPro.Core.Tests.Viewing;

public sealed class DocumentLayoutTests
{
    private const double K = DocumentLayout.UnitsPerPointAt100Percent;
    private static readonly PdfSize A4 = new(595, 842);
    private static readonly PdfSize A4Landscape = new(842, 595);
    private static readonly LayoutSpacing Spacing = new(Margin: 10, RowGap: 8, ColumnGap: 6);

    [Fact]
    public void Single_StacksPagesWithGapsAndCentresThem()
    {
        var layout = DocumentLayout.Create([A4, A4Landscape, A4], zoom: 1, PageLayoutMode.Single, viewportWidth: 2000, Spacing);

        var p0 = layout.GetPageBounds(0);
        var p1 = layout.GetPageBounds(1);
        var p2 = layout.GetPageBounds(2);

        p0.Y.ShouldBe(10);
        p0.Height.ShouldBe(842 * K, 1e-9);
        p1.Y.ShouldBe(p0.Bottom + 8, 1e-9);
        p2.Y.ShouldBe(p1.Bottom + 8, 1e-9);
        layout.Extent.Width.ShouldBe(2000, "narrow content is centred in the viewport");
        (p0.X + (p0.Width / 2)).ShouldBe(1000, 1e-9);
        (p1.X + (p1.Width / 2)).ShouldBe(1000, 1e-9);
        layout.Extent.Height.ShouldBe(p2.Bottom + 10, 1e-9);
    }

    [Fact]
    public void Single_WideContentExtendsTheExtentWithMargins()
    {
        var layout = DocumentLayout.Create([A4, A4Landscape], zoom: 2, PageLayoutMode.Single, viewportWidth: 500, Spacing);

        layout.Extent.Width.ShouldBe((842 * K * 2) + 20, 1e-9);
        layout.GetPageBounds(1).X.ShouldBe(10, 1e-9);
    }

    [Fact]
    public void Facing_PairsPagesAroundTheSpine()
    {
        var layout = DocumentLayout.Create([A4, A4, A4, A4, A4], zoom: 1, PageLayoutMode.Facing, viewportWidth: 3000, Spacing);
        var spine = layout.Extent.Width / 2;

        layout.Rows.Select(r => (r.FirstPage, r.PageCount)).ShouldBe([(0, 2), (2, 2), (4, 1)]);
        layout.GetPageBounds(0).Right.ShouldBe(spine - 3, 1e-9);
        layout.GetPageBounds(1).X.ShouldBe(spine + 3, 1e-9);
        layout.GetPageBounds(4).Right.ShouldBe(spine - 3, 1e-9, "a last odd page sits on the left");
        layout.GetPageBounds(0).Y.ShouldBe(layout.GetPageBounds(1).Y);
    }

    [Fact]
    public void FacingWithCover_PutsTheFirstPageAloneOnTheRight()
    {
        var layout = DocumentLayout.Create([A4, A4, A4, A4], zoom: 1, PageLayoutMode.FacingWithCover, viewportWidth: 3000, Spacing);
        var spine = layout.Extent.Width / 2;

        layout.Rows.Select(r => (r.FirstPage, r.PageCount)).ShouldBe([(0, 1), (1, 2), (3, 1)]);
        layout.GetPageBounds(0).X.ShouldBe(spine + 3, 1e-9);
        layout.GetPageBounds(1).Right.ShouldBe(spine - 3, 1e-9);
        layout.GetPageBounds(2).X.ShouldBe(spine + 3, 1e-9);
    }

    [Fact]
    public void Facing_RowHeightIsTheTallestPageAndPagesAreCentredVertically()
    {
        var layout = DocumentLayout.Create([A4, A4Landscape], zoom: 1, PageLayoutMode.Facing, viewportWidth: 3000, Spacing);

        layout.Rows[0].Height.ShouldBe(842 * K, 1e-9);
        var landscape = layout.GetPageBounds(1);
        (landscape.Y + (landscape.Height / 2)).ShouldBe(10 + (842 * K / 2), 1e-9);
    }

    [Fact]
    public void VisiblePages_MatchABruteForceScanOnMixedSizes()
    {
        var random = new Random(42);
        var sizes = Enumerable.Range(0, 300).Select(_ => random.Next(3) == 0 ? A4Landscape : A4).ToArray();
        foreach (var mode in Enum.GetValues<PageLayoutMode>())
        {
            var layout = DocumentLayout.Create(sizes, zoom: 0.37, mode, viewportWidth: 1200, Spacing);
            for (var i = 0; i < 100; i++)
            {
                var viewport = new ViewRect(0, random.NextDouble() * layout.Extent.Height, 1200, 300 + (random.NextDouble() * 900));

                var expectedRows = layout.Rows.Where(r => r.Bottom >= viewport.Y && r.Top <= viewport.Bottom).ToList();
                var actual = layout.GetVisiblePages(viewport);

                if (expectedRows.Count == 0)
                {
                    actual.ShouldBeNull();
                }
                else
                {
                    actual.ShouldBe((expectedRows[0].FirstPage, expectedRows[^1].LastPage), $"{mode} at y={viewport.Y}");
                }
            }
        }
    }

    [Fact]
    public void PageAt_AndNearestPage()
    {
        var layout = DocumentLayout.Create([A4, A4, A4], zoom: 1, PageLayoutMode.Single, viewportWidth: 1000, Spacing);
        var page1 = layout.GetPageBounds(1);
        var gapPoint = new ViewPoint(page1.X + 5, page1.Y - 3);

        layout.GetPageAt(new ViewPoint(page1.X + 10, page1.Y + 10)).ShouldBe(1);
        layout.GetPageAt(gapPoint).ShouldBeNull();
        layout.GetNearestPage(gapPoint).ShouldBe(1, "3 units above page 1 is closer than 5 below page 0");
        layout.GetPageAt(new ViewPoint(2, page1.Y + 10)).ShouldBeNull("left margin");
        layout.GetNearestPage(new ViewPoint(2, page1.Y + 10)).ShouldBe(1);
        layout.GetNearestPage(new ViewPoint(500, layout.Extent.Height + 500)).ShouldBe(2);
    }

    [Fact]
    public void CurrentPage_IsTheMostVisibleOne()
    {
        var layout = DocumentLayout.Create([A4, A4, A4], zoom: 1, PageLayoutMode.Single, viewportWidth: 1000, Spacing);
        var page1 = layout.GetPageBounds(1);

        layout.GetCurrentPage(new ViewRect(0, page1.Y - 100, 1000, 400)).ShouldBe(1);
        layout.GetCurrentPage(new ViewRect(0, page1.Y - 1000, 1000, 1050)).ShouldBe(0);
        layout.GetCurrentPage(new ViewRect(0, 0, 1000, 5)).ShouldBe(0, "only the top margin is visible");
    }

    [Fact]
    public void OffsetToShow_PutsThePointNearTheTopAndClamps()
    {
        var layout = DocumentLayout.Create(Enumerable.Repeat(A4, 10).ToArray(), zoom: 1, PageLayoutMode.Single, viewportWidth: 1000, Spacing);
        var viewport = new ViewSize(1000, 700);

        var offset = layout.OffsetToShow(3, new ViewPoint(0, 100), currentOffsetX: 0, viewport);
        offset.Y.ShouldBe(layout.GetPageBounds(3).Y + 100 - 4, 1e-9);

        var nearEnd = layout.OffsetToShow(9, new ViewPoint(0, 1100), 0, viewport);
        nearEnd.Y.ShouldBe(layout.MaxOffset(viewport).Y, "cannot scroll past the end");
        layout.OffsetToShow(0, new ViewPoint(0, 0), 0, viewport).Y.ShouldBe(0, 1e-9);
    }

    [Theory]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 0.5)]
    [InlineData(0.75, 0.825)]
    [InlineData(1.0, 1.1)]
    public void AnchoredOffset_KeepsTheSamePagePointUnderTheAnchor(double zoomBefore, double zoomAfter)
    {
        var sizes = Enumerable.Range(0, 50).Select(i => i % 5 == 3 ? A4Landscape : A4).ToArray();
        var viewport = new ViewSize(1400, 900);
        var before = DocumentLayout.Create(sizes, zoomBefore, PageLayoutMode.Single, viewport.Width, Spacing);
        var after = DocumentLayout.Create(sizes, zoomAfter, PageLayoutMode.Single, viewport.Width, Spacing);
        var offsetBefore = new ViewPoint(0, before.GetPageBounds(20).Y + 40);
        var anchor = new ViewPoint(viewport.Width / 2 + 30, 300);

        var offsetAfter = DocumentLayout.AnchoredOffset(before, after, offsetBefore, anchor, viewport);

        var page = before.GetPageAt(offsetBefore + anchor)!.Value;
        var pointBefore = PagePoint(before, page, offsetBefore + anchor, zoomBefore);
        var pointAfter = PagePoint(after, page, offsetAfter + anchor, zoomAfter);
        pointAfter.Y.ShouldBe(pointBefore.Y, 0.01, "same spot of the page (in points) under the mouse");
        if (after.Extent.Width > viewport.Width)
        {
            pointAfter.X.ShouldBe(pointBefore.X, 0.01);
        }
        else
        {
            offsetAfter.X.ShouldBe(0, "content narrower than the viewport stays centred");
        }
    }

    [Fact]
    public void AnchoredOffset_ClampsAtTheTopOfTheDocument()
    {
        var sizes = Enumerable.Repeat(A4, 5).ToArray();
        var viewport = new ViewSize(1000, 800);
        var before = DocumentLayout.Create(sizes, 1, PageLayoutMode.Single, viewport.Width, Spacing);
        var after = DocumentLayout.Create(sizes, 0.5, PageLayoutMode.Single, viewport.Width, Spacing);

        var offset = DocumentLayout.AnchoredOffset(before, after, new ViewPoint(0, 0), new ViewPoint(500, 20), viewport);

        offset.Y.ShouldBeGreaterThanOrEqualTo(0);
        offset.X.ShouldBe(0);
    }

    [Fact]
    public void EmptyDocument_HasOnlyMargins()
    {
        var layout = DocumentLayout.Create([], 1, PageLayoutMode.Single, 800, Spacing);

        layout.PageCount.ShouldBe(0);
        layout.Extent.ShouldBe(new ViewSize(800, 20));
        layout.GetVisiblePages(new ViewRect(0, 0, 800, 600)).ShouldBeNull();
        layout.GetCurrentPage(new ViewRect(0, 0, 800, 600)).ShouldBeNull();
    }

    [Fact]
    public void Scale_IsZoomTimesDipPerPoint()
        => DocumentLayout.Create([A4], 1.5, PageLayoutMode.Single, 800).Scale.ShouldBe(1.5 * 96 / 72, 1e-12);

    private static PdfPoint PagePoint(DocumentLayout layout, int page, ViewPoint surfacePoint, double zoom)
    {
        var bounds = layout.GetPageBounds(page);
        return new PdfPoint((surfacePoint.X - bounds.X) / (zoom * K), (surfacePoint.Y - bounds.Y) / (zoom * K));
    }
}

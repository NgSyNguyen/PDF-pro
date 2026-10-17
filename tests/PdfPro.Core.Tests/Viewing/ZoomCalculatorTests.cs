using PdfPro.Core.Geometry;
using PdfPro.Core.Viewing;

namespace PdfPro.Core.Tests.Viewing;

public sealed class ZoomCalculatorTests
{
    private const double K = DocumentLayout.UnitsPerPointAt100Percent;
    private static readonly PdfSize A4 = new(595, 842);
    private static readonly LayoutSpacing Spacing = new(Margin: 10, RowGap: 8, ColumnGap: 6);

    [Theory]
    [InlineData(1.00, 1.25)]
    [InlineData(1.10, 1.25)]
    [InlineData(0.05, 0.10)]
    [InlineData(8.00, 8.00)]
    [InlineData(1.2499, 1.50)] // displayed as 125 %, so it counts as that preset
    public void ZoomIn_GoesToTheNextPreset(double current, double expected)
        => ZoomCalculator.ZoomIn(current).ShouldBe(expected);

    [Theory]
    [InlineData(1.00, 0.75)]
    [InlineData(1.10, 1.00)]
    [InlineData(0.10, 0.10)]
    [InlineData(20.0, 8.00)]
    public void ZoomOut_GoesToThePreviousPreset(double current, double expected)
        => ZoomCalculator.ZoomOut(current).ShouldBe(expected);

    [Theory]
    [InlineData(1.0, 1, 1.1)]
    [InlineData(1.0, -1, 1 / 1.1)]
    [InlineData(1.0, 3, 1.331)]
    [InlineData(7.9, 5, 8.0)]
    [InlineData(0.11, -5, 0.10)]
    public void ZoomByWheel_MultipliesPerNotchAndClamps(double current, double notches, double expected)
        => ZoomCalculator.ZoomByWheel(current, notches).ShouldBe(expected, 1e-9);

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.01, 0.10)]
    [InlineData(100, 8.0)]
    public void Clamp_KeepsTheZoomInRange(double zoom, double expected) => ZoomCalculator.Clamp(zoom).ShouldBe(expected);

    [Fact]
    public void FitWidth_Single_FillsTheViewportExactly()
    {
        var zoom = ZoomCalculator.FitWidth([A4, new PdfSize(500, 700)], PageLayoutMode.Single, 1000, Spacing);

        var layout = DocumentLayout.Create([A4, new PdfSize(500, 700)], zoom, PageLayoutMode.Single, 1000, Spacing);
        layout.Extent.Width.ShouldBe(1000, 1e-6);
        layout.GetPageBounds(0).Width.ShouldBe(1000 - 20, 1e-6);
        zoom.ShouldBe((1000 - 20) / (595 * K), 1e-12);
    }

    [Fact]
    public void FitWidth_Facing_FitsTwoPagesAndTheGap()
    {
        var zoom = ZoomCalculator.FitWidth([A4, A4, A4], PageLayoutMode.Facing, 1200, Spacing);

        DocumentLayout.Create([A4, A4, A4], zoom, PageLayoutMode.Facing, 1200, Spacing).Extent.Width.ShouldBe(1200, 1e-6);
    }

    [Fact]
    public void FitPage_IsLimitedByTheHeightForPortraitPages()
    {
        var viewport = new ViewSize(1600, 800);

        var zoom = ZoomCalculator.FitPage([A4], PageLayoutMode.Single, 0, viewport, Spacing);

        zoom.ShouldBe((800 - 8) / (842 * K), 1e-12);
        (842 * K * zoom).ShouldBeLessThanOrEqualTo(viewport.Height);
    }

    [Fact]
    public void FitPage_IsLimitedByTheWidthForWidePages()
    {
        var zoom = ZoomCalculator.FitPage([new PdfSize(1684, 595)], PageLayoutMode.Single, 0, new ViewSize(800, 900), Spacing);

        zoom.ShouldBe((800 - 20) / (1684 * K), 1e-12);
    }

    [Theory]
    [InlineData(PageLayoutMode.Single, 3, 3, 1)]
    [InlineData(PageLayoutMode.Facing, 3, 2, 2)]
    [InlineData(PageLayoutMode.Facing, 4, 4, 1)]
    [InlineData(PageLayoutMode.FacingWithCover, 0, 0, 1)]
    [InlineData(PageLayoutMode.FacingWithCover, 1, 1, 2)]
    [InlineData(PageLayoutMode.FacingWithCover, 2, 1, 2)]
    [InlineData(PageLayoutMode.FacingWithCover, 4, 3, 2)]
    public void RowOf_MatchesTheLayoutRows(PageLayoutMode mode, int page, int expectedFirst, int expectedCount)
    {
        ZoomCalculator.RowOf(page, 5, mode).ShouldBe((expectedFirst, expectedCount));

        var layout = DocumentLayout.Create(Enumerable.Repeat(A4, 5).ToArray(), 1, mode, 2000, Spacing);
        var row = layout.Rows[layout.GetRowOfPage(page)];
        (row.FirstPage, row.PageCount).ShouldBe((expectedFirst, expectedCount));
    }

    [Fact]
    public void EmptyDocument_FitsAtHundredPercent()
    {
        ZoomCalculator.FitWidth([], PageLayoutMode.Single, 900).ShouldBe(1.0);
        ZoomCalculator.FitPage([], PageLayoutMode.Single, 0, new ViewSize(900, 700)).ShouldBe(1.0);
    }
}

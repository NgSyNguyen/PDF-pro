using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;

namespace PdfPro.Core.Tests.Organizing;

public sealed class MaterializedGeometryTests
{
    private static readonly PageGeometry Source = new(new PdfRect(50, 100, 450, 700), 90);

    [Fact]
    public void AnUneditedPage_KeepsItsSourceGeometry()
        => MaterializedGeometry.Of(new PageRef("s0", 0, 0, null, Guid.NewGuid()), Source).ShouldBe(Source);

    [Fact]
    public void ThePlansRotation_AddsToTheSourceRotation()
        => MaterializedGeometry.Of(new PageRef("s0", 0, 270, null, Guid.NewGuid()), Source).RotationDegrees.ShouldBe(0);

    [Fact]
    public void ACrop_ShownOnTheTurnedPage_BecomesTheBoxInPdfSpace()
    {
        // Shown turned 90°: 600 wide, 400 high. Keep the shown top-left 100 × 50 corner.
        var page = new PageRef("s0", 0, 0, new PageCrop(0, 0, 100, 50), Guid.NewGuid());

        var geometry = MaterializedGeometry.Of(page, Source);

        geometry.Box.ShouldBe(new PdfRect(50, 100, 100, 200));
        geometry.DisplaySize.ShouldBe(new PdfSize(100, 50));
        var before = new PageCoordinateMapper(Source, 1).ScreenToPdf(new ViewPoint(10, 10));
        var after = new PageCoordinateMapper(geometry, 1).ScreenToPdf(new ViewPoint(10, 10));
        after.ShouldBe(before, "the kept corner shows the same spot of the page");
    }

    [Fact]
    public void BlankPages_StartAtTheOrigin()
    {
        var page = PageRef.Blank(new PdfSize(300, 200), Guid.NewGuid()).WithRotation(90);

        MaterializedGeometry.Of(page, null).ShouldBe(new PageGeometry(new PdfRect(0, 0, 300, 200), 90));
        Should.Throw<ArgumentNullException>(() => MaterializedGeometry.Of(new PageRef("s0", 0, 0, null, Guid.NewGuid()), null));
    }
}

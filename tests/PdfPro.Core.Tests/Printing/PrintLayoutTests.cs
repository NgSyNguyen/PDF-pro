using PdfPro.Core.Geometry;
using PdfPro.Core.Printing;

namespace PdfPro.Core.Tests.Printing;

public sealed class PrintLayoutTests
{
    private const double K = PrintLayout.UnitsPerPoint;
    private static readonly PdfSize A4 = new(595, 842);
    private static readonly PdfSize A4Landscape = new(842, 595);

    // US Letter (8.5 x 11 in) at 96 units per inch with a quarter-inch margin all round.
    private static readonly ViewRect LetterPrintable = new(24, 24, 816 - 48, 1056 - 48);

    [Fact]
    public void PageOfTheSameShape_FillsTheAreaWithoutRotation()
    {
        var area = new ViewRect(0, 0, 595 * K, 842 * K);

        var placement = PrintLayout.Fit(A4, area);

        placement.Rotated.ShouldBeFalse();
        placement.Scale.ShouldBe(K, 1e-9);
        placement.Bounds.X.ShouldBe(0, 1e-9);
        placement.Bounds.Y.ShouldBe(0, 1e-9);
        placement.Bounds.Width.ShouldBe(area.Width, 1e-9);
        placement.Bounds.Height.ShouldBe(area.Height, 1e-9);
    }

    [Fact]
    public void Page_IsScaledToFitAndCentredInThePrintableArea()
    {
        var placement = PrintLayout.Fit(A4, LetterPrintable);

        placement.Rotated.ShouldBeFalse();
        placement.Scale.ShouldBe(Math.Min(768 / 595.0, 1008 / 842.0), 1e-9);
        placement.Bounds.Width.ShouldBeLessThanOrEqualTo(LetterPrintable.Width + 1e-9);
        placement.Bounds.Height.ShouldBeLessThanOrEqualTo(LetterPrintable.Height + 1e-9);
        (placement.Bounds.X + (placement.Bounds.Width / 2)).ShouldBe(LetterPrintable.X + (LetterPrintable.Width / 2), 1e-9);
        (placement.Bounds.Y + (placement.Bounds.Height / 2)).ShouldBe(LetterPrintable.Y + (LetterPrintable.Height / 2), 1e-9);
        (placement.Bounds.Width / placement.Bounds.Height).ShouldBe(595 / 842.0, 1e-9, "no distortion");
    }

    [Fact]
    public void SmallPage_IsEnlarged()
    {
        var placement = PrintLayout.Fit(new PdfSize(200, 300), LetterPrintable);

        placement.Scale.ShouldBeGreaterThan(K);
        Math.Max(placement.Bounds.Width - LetterPrintable.Width, placement.Bounds.Height - LetterPrintable.Height).ShouldBe(0, 1e-9, "one side touches the area");
    }

    [Fact]
    public void LandscapePageOnPortraitPaper_IsRotated()
    {
        var placement = PrintLayout.Fit(A4Landscape, LetterPrintable);

        placement.Rotated.ShouldBeTrue();
        placement.Bounds.Height.ShouldBeGreaterThan(placement.Bounds.Width, "the turned page is portrait");
        placement.Scale.ShouldBe(Math.Min(768 / 595.0, 1008 / 842.0), 1e-9);
    }

    [Fact]
    public void PortraitPageOnLandscapePaper_IsRotated()
    {
        var landscapeArea = new ViewRect(10, 10, 1036, 796);

        var placement = PrintLayout.Fit(A4, landscapeArea);

        placement.Rotated.ShouldBeTrue();
        placement.Bounds.Width.ShouldBeGreaterThan(placement.Bounds.Height);
    }

    [Fact]
    public void SquarePagesOrAreas_AreNeverRotated()
    {
        PrintLayout.Fit(new PdfSize(500, 500), LetterPrintable).Rotated.ShouldBeFalse();
        PrintLayout.Fit(A4Landscape, new ViewRect(0, 0, 800, 800)).Rotated.ShouldBeFalse();
    }

    [Fact]
    public void InvalidInput_IsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PrintLayout.Fit(new PdfSize(0, 100), LetterPrintable));
        Should.Throw<ArgumentOutOfRangeException>(() => PrintLayout.Fit(A4, new ViewRect(0, 0, 0, 100)));
    }
}

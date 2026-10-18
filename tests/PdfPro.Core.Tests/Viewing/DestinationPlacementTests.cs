using PdfPro.Core.Geometry;
using PdfPro.Core.Viewing;

namespace PdfPro.Core.Tests.Viewing;

public sealed class DestinationPlacementTests
{
    private static readonly PageGeometry Upright = new(new PdfRect(0, 0, 600, 800), 0);

    [Fact]
    public void Upright_MapsTheTopLeftCornerAtTheScale()
    {
        var point = DestinationPlacement.PointInPage(Upright, left: 100, top: 700, scale: 2);

        point.ShouldBe(new ViewPoint(200, 200));
    }

    [Fact]
    public void MissingCoordinates_FallBackToThePageTopLeft()
    {
        DestinationPlacement.PointInPage(Upright, null, null, 1.5).ShouldBe(new ViewPoint(0, 0));
        DestinationPlacement.PointInPage(Upright, null, 500, 1).ShouldBe(new ViewPoint(0, 300));
        DestinationPlacement.PointInPage(Upright, 250, null, 1).ShouldBe(new ViewPoint(250, 0));
    }

    [Fact]
    public void BoxNotAtTheOrigin_IsMeasuredFromTheVisibleBox()
    {
        var cropped = new PageGeometry(new PdfRect(50, 100, 650, 900), 0);

        DestinationPlacement.PointInPage(cropped, left: 150, top: 800, scale: 1).ShouldBe(new ViewPoint(100, 100));
    }

    [Fact]
    public void RotatedQuarterTurn_FollowsTheDisplayedPage()
    {
        var rotated = new PageGeometry(new PdfRect(0, 0, 600, 800), 90);

        // Shown clockwise: PDF y runs left to right and PDF x runs top to bottom.
        DestinationPlacement.PointInPage(rotated, left: 100, top: 700, scale: 1).ShouldBe(new ViewPoint(700, 100));
        DestinationPlacement.PointInPage(rotated, null, null, 1).ShouldBe(new ViewPoint(0, 0));
    }

    [Fact]
    public void UpsideDown_MissingCoordinatesStillMeanTheDisplayedTop()
    {
        var upsideDown = new PageGeometry(new PdfRect(0, 0, 600, 800), 180);

        DestinationPlacement.PointInPage(upsideDown, null, null, 1).ShouldBe(new ViewPoint(0, 0));
        DestinationPlacement.PointInPage(upsideDown, left: 100, top: 700, scale: 1).ShouldBe(new ViewPoint(500, 700));
    }

    [Theory]
    [InlineData(2000, 0)]
    [InlineData(-50, 800)]
    public void OutsideThePage_IsClamped(double top, double expectedY)
    {
        DestinationPlacement.PointInPage(Upright, left: -10, top: top, scale: 1).ShouldBe(new ViewPoint(0, expectedY));
        DestinationPlacement.PointInPage(Upright, left: 5000, top: 400, scale: 1).X.ShouldBe(600);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteCoordinates_AreIgnored(double value)
    {
        DestinationPlacement.PointInPage(Upright, value, value, 1).ShouldBe(new ViewPoint(0, 0));
    }
}

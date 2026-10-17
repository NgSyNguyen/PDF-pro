using PdfPro.Core.Geometry;

namespace PdfPro.Core.Tests.Geometry;

public sealed class PdfGeometryTests
{
    [Fact]
    public void PdfRect_NormalizesCorners()
    {
        var rect = new PdfRect(559, 770, 36, 72);

        rect.ShouldBe(new PdfRect(36, 72, 559, 770));
        (rect.Left, rect.Bottom, rect.Right, rect.Top).ShouldBe((36d, 72d, 559d, 770d));
        rect.Width.ShouldBe(523);
        rect.Height.ShouldBe(698);
        rect.Size.ShouldBe(new PdfSize(523, 698));
        rect.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void PdfRect_ContainsIncludesEdges()
    {
        var rect = new PdfRect(0, 0, 10, 20);

        rect.Contains(new PdfPoint(0, 0)).ShouldBeTrue();
        rect.Contains(new PdfPoint(10, 20)).ShouldBeTrue();
        rect.Contains(new PdfPoint(10.01, 5)).ShouldBeFalse();
        new PdfRect(5, 5, 5, 9).IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(450, 90)]
    [InlineData(-90, 270)]
    [InlineData(-360, 0)]
    [InlineData(720, 0)]
    public void PageGeometry_NormalizesRotation(int degrees, int expected)
        => new PageGeometry(new PdfRect(0, 0, 100, 200), degrees).RotationDegrees.ShouldBe(expected);

    [Fact]
    public void PageGeometry_RejectsOddAnglesAndEmptyBoxes()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new PageGeometry(new PdfRect(0, 0, 10, 10), 45));
        Should.Throw<ArgumentException>(() => new PageGeometry(new PdfRect(0, 0, 0, 10), 0));
    }

    [Theory]
    [InlineData(0, 400, 600)]
    [InlineData(90, 600, 400)]
    [InlineData(180, 400, 600)]
    [InlineData(270, 600, 400)]
    public void PageGeometry_DisplaySizeSwapsForQuarterTurns(int rotation, double width, double height)
        => new PageGeometry(new PdfRect(50, 100, 450, 700), rotation).DisplaySize.ShouldBe(new PdfSize(width, height));

    [Theory]
    [InlineData(595, 1.5, 893)]
    [InlineData(595, 1.0, 595)]
    [InlineData(842, 0.25, 211)]
    [InlineData(0.1, 1.0, 1)]
    public void PixelSize_RoundsHalfAwayFromZeroWithMinimumOne(double points, double scale, int expected)
        => PixelSize.FromPoints(points, scale).ShouldBe(expected);

    [Fact]
    public void PixelRect_IntersectionAndContainment()
    {
        var page = new PixelRect(0, 0, 100, 200);

        PixelRect.Intersect(page, new PixelRect(80, 150, 50, 100)).ShouldBe(new PixelRect(80, 150, 20, 50));
        PixelRect.Intersect(page, new PixelRect(200, 0, 10, 10)).IsEmpty.ShouldBeTrue();
        page.Contains(new PixelRect(10, 10, 90, 190)).ShouldBeTrue();
        page.Contains(new PixelRect(10, 10, 91, 10)).ShouldBeFalse();
        new PixelRect(0, 0, 3000, 4000).Area.ShouldBe(12_000_000);
        new PixelRect(0, 0, -1, 10).Area.ShouldBe(0);
    }
}

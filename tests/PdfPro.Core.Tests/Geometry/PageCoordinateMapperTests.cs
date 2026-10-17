using PdfPro.Core.Geometry;

namespace PdfPro.Core.Tests.Geometry;

public sealed class PageCoordinateMapperTests
{
    // A crop box that does not start at (0, 0): 400 x 600 pt.
    private static readonly PdfRect OffsetBox = new(50, 100, 450, 700);

    public static TheoryData<int, double> RotationsAndScales()
    {
        var data = new TheoryData<int, double>();
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            foreach (var scale in new[] { 0.5, 1.0, 96.0 / 72.0 * 2.5 })
            {
                data.Add(rotation, scale);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RotationsAndScales))]
    public void RoundTrip_PdfToScreenToPdf_IsExact(int rotation, double scale)
    {
        var mapper = new PageCoordinateMapper(OffsetBox, rotation, scale);
        var random = new Random(rotation + (int)(scale * 100));

        for (var i = 0; i < 200; i++)
        {
            var point = new PdfPoint(OffsetBox.Left + (random.NextDouble() * OffsetBox.Width), OffsetBox.Bottom + (random.NextDouble() * OffsetBox.Height));

            var back = mapper.ScreenToPdf(mapper.PdfToScreen(point));

            back.X.ShouldBe(point.X, 0.01);
            back.Y.ShouldBe(point.Y, 0.01);
        }
    }

    [Theory]
    [MemberData(nameof(RotationsAndScales))]
    public void RoundTrip_ScreenToPdfToScreen_IsExact(int rotation, double scale)
    {
        var mapper = new PageCoordinateMapper(OffsetBox, rotation, scale);
        var view = mapper.ViewSize;
        var random = new Random(rotation * 7);

        for (var i = 0; i < 200; i++)
        {
            var point = new ViewPoint(random.NextDouble() * view.Width, random.NextDouble() * view.Height);

            var back = mapper.PdfToScreen(mapper.ScreenToPdf(point));

            back.X.ShouldBe(point.X, 0.01);
            back.Y.ShouldBe(point.Y, 0.01);
        }
    }

    /// <summary>The page's top-left corner (in page space) moves clockwise with the rotation.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(90, 600, 0)]
    [InlineData(180, 400, 600)]
    [InlineData(270, 0, 400)]
    public void PageTopLeft_LandsOnTheRotatedCorner(int rotation, double expectedX, double expectedY)
    {
        var mapper = new PageCoordinateMapper(OffsetBox, rotation, 1.0);

        var corner = mapper.PdfToScreen(new PdfPoint(OffsetBox.Left, OffsetBox.Top));

        corner.X.ShouldBe(expectedX, 1e-9);
        corner.Y.ShouldBe(expectedY, 1e-9);
    }

    [Theory]
    [InlineData(0, 400, 600)]
    [InlineData(90, 600, 400)]
    [InlineData(180, 400, 600)]
    [InlineData(270, 600, 400)]
    public void ViewSize_SwapsForQuarterTurns(int rotation, double width, double height)
    {
        var mapper = new PageCoordinateMapper(OffsetBox, rotation, 2.0);

        mapper.ViewSize.ShouldBe(new ViewSize(width * 2, height * 2));
    }

    [Theory]
    [MemberData(nameof(RotationsAndScales))]
    public void Corners_OfTheBox_MapToCornersOfTheView(int rotation, double scale)
    {
        var mapper = new PageCoordinateMapper(OffsetBox, rotation, scale);

        var bounds = mapper.PdfRectToScreen(OffsetBox);

        bounds.X.ShouldBe(0, 1e-9);
        bounds.Y.ShouldBe(0, 1e-9);
        bounds.Width.ShouldBe(mapper.ViewSize.Width, 1e-9);
        bounds.Height.ShouldBe(mapper.ViewSize.Height, 1e-9);
    }

    [Theory]
    [MemberData(nameof(RotationsAndScales))]
    public void Rectangles_RoundTripAndStayNormalized(int rotation, double scale)
    {
        var mapper = new PageCoordinateMapper(OffsetBox, rotation, scale);
        var rect = new PdfRect(120, 300, 260, 340); // e.g. a highlighted line

        var view = mapper.PdfRectToScreen(rect);
        var back = mapper.ScreenRectToPdf(view);

        view.Width.ShouldBeGreaterThan(0);
        view.Height.ShouldBeGreaterThan(0);
        back.Left.ShouldBe(rect.Left, 0.01);
        back.Bottom.ShouldBe(rect.Bottom, 0.01);
        back.Right.ShouldBe(rect.Right, 0.01);
        back.Top.ShouldBe(rect.Top, 0.01);
    }

    [Fact]
    public void Geometry_Constructor_UsesBoxAndRotation()
    {
        var mapper = new PageCoordinateMapper(new PageGeometry(OffsetBox, -90), 1.0);

        mapper.RotationDegrees.ShouldBe(270);
        mapper.PdfToScreen(new PdfPoint(OffsetBox.Left, OffsetBox.Top)).ShouldBe(new ViewPoint(0, 400));
    }

    [Fact]
    public void InvalidArguments_AreRejected()
    {
        Should.Throw<ArgumentException>(() => new PageCoordinateMapper(new PdfRect(0, 0, 0, 10), 0, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => new PageCoordinateMapper(OffsetBox, 0, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new PageCoordinateMapper(OffsetBox, 0, double.NaN));
        Should.Throw<ArgumentOutOfRangeException>(() => new PageCoordinateMapper(OffsetBox, 45, 1));
    }
}

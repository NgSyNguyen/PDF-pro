using System.Globalization;
using PdfPro.Core.Geometry;
using PdfPro.Core.Measuring;

namespace PdfPro.Core.Tests.Measuring;

/// <summary>Distances, lines and areas on a page, at a drawing's scale.</summary>
public sealed class MeasurementsTests
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    [Fact]
    public void TheWidthOfAnA4Page_Is210Millimetres()
    {
        var result = Measurements.Measure(MeasureKind.Distance, [new PdfPoint(0, 0), new PdfPoint(595.2756, 0)], LengthUnit.Millimetre)!;

        result.Length.ShouldBe(210, 0.001);
        result.Angle.ShouldBe(0);
        result.Area.ShouldBeNull();
        Measurements.Format(result.Length, result.Unit, culture: Vietnamese).ShouldBe("210 mm");
    }

    [Fact]
    public void AtAScale_LengthsAndAreasAreReal()
    {
        // A 2 cm × 3 cm rectangle on a 1 : 100 plan is a 2 m × 3 m room.
        var cm = 720 / 25.4;
        PdfPoint[] room = [new(0, 0), new(2 * cm, 0), new(2 * cm, 3 * cm), new(0, 3 * cm)];

        var result = Measurements.Measure(MeasureKind.Area, room, LengthUnit.Metre, scale: 100)!;

        result.Area!.Value.ShouldBe(6, 1e-9);
        result.Length.ShouldBe(10, 1e-9, "its perimeter");
        Measurements.Format(result.Area.Value, LengthUnit.Metre, squared: true, Vietnamese).ShouldBe("6 m²");
    }

    [Fact]
    public void ALine_IsMeasuredAlongItsPoints_AndAPolygonCountsItsClosingSide()
    {
        PdfPoint[] path = [new(0, 0), new(72, 0), new(72, 72)];

        Measurements.Measure(MeasureKind.Perimeter, path, LengthUnit.Inch)!.Length.ShouldBe(2);
        Measurements.Measure(MeasureKind.Area, path, LengthUnit.Inch)!.Length.ShouldBe(2 + Math.Sqrt(2), 1e-9);
        Measurements.Measure(MeasureKind.Area, path, LengthUnit.Inch)!.Area!.Value.ShouldBe(0.5, 1e-9);
        Measurements.Measure(MeasureKind.Area, path.Take(2).ToList(), LengthUnit.Inch).ShouldBeNull("an area needs three points");
        Measurements.Measure(MeasureKind.Distance, [path[0]], LengthUnit.Inch).ShouldBeNull();
    }

    [Fact]
    public void TheAngle_IsTakenOnThePageAsShown()
    {
        PdfPoint[] up = [new(0, 0), new(0, 10)];

        Measurements.Measure(MeasureKind.Distance, up, LengthUnit.Point)!.Angle.ShouldBe(90);

        // On a page turned a quarter clockwise, the PDF's "up" is shown pointing right.
        Measurements.Measure(MeasureKind.Distance, up, LengthUnit.Point, shown: p => new ViewPoint(p.Y, p.X))!.Angle.ShouldBe(0);
    }

    [Theory]
    [InlineData(10, 1, 10, 0)]
    [InlineData(10, 9, 9.5, 9.5)]
    [InlineData(-1, 10, 0, 10.05)]
    public void Shift_SnapsToTheNearest45Degrees(double x, double y, double snappedX, double snappedY)
    {
        var snapped = Measurements.Snap(new PdfPoint(0, 0), new PdfPoint(x, y));

        snapped.X.ShouldBe(snappedX, 0.1);
        snapped.Y.ShouldBe(snappedY, 0.1);
    }

    [Theory]
    [InlineData(125.4, LengthUnit.Millimetre, false, "125,4 mm")]
    [InlineData(3.256, LengthUnit.Centimetre, true, "3,26 cm²")]
    [InlineData(12345.67, LengthUnit.Metre, false, "12.345,7 m")]
    [InlineData(0.5, LengthUnit.Inch, false, "0,5 in")]
    public void Values_AreWrittenTheVietnameseWay(double value, LengthUnit unit, bool squared, string expected)
        => Measurements.Format(value, unit, squared, Vietnamese).ShouldBe(expected);
}

using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;

namespace PdfPro.Core.Tests.Annotations;

public sealed class AnnotationHitTestTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));
    private static readonly RgbColor Red = RgbColor.Red;

    [Fact]
    public void MarkedText_IsPickedOnItsLines_NotBetweenThem()
    {
        var highlight = new MarkupAnnotation("h", Red, 1, "", Created, Created, "", MarkupKind.Highlight,
            [TextQuad.Of(new PdfRect(72, 700, 300, 712)), TextQuad.Of(new PdfRect(72, 680, 150, 692))]);

        AnnotationHitTest.Contains(highlight, new PdfPoint(200, 706), 0).ShouldBeTrue();
        AnnotationHitTest.Contains(highlight, new PdfPoint(100, 685), 0).ShouldBeTrue();
        AnnotationHitTest.Contains(highlight, new PdfPoint(200, 685), 0).ShouldBeFalse("the second line is shorter");
        AnnotationHitTest.Contains(highlight, new PdfPoint(100, 696), 0).ShouldBeFalse("between the lines");
    }

    [Fact]
    public void Strokes_AndLines_ArePickedAlongTheirPath_WithSlack()
    {
        var ink = new InkAnnotation("i", Red, 1, "", Created, Created, "", 2, [[new PdfPoint(0, 0), new PdfPoint(100, 0)], [new PdfPoint(0, 50)]]);
        var line = new LineAnnotation("l", Red, 1, "", Created, Created, "", new PdfPoint(0, 0), new PdfPoint(100, 100), 1);

        AnnotationHitTest.Contains(ink, new PdfPoint(50, 3), 2.5).ShouldBeTrue("width 2 + slack 2.5");
        AnnotationHitTest.Contains(ink, new PdfPoint(50, 4), 2.5).ShouldBeFalse();
        AnnotationHitTest.Contains(ink, new PdfPoint(1, 51), 2).ShouldBeTrue("a dot is a stroke of one point");
        AnnotationHitTest.Contains(line, new PdfPoint(50, 52), 3).ShouldBeTrue();
        AnnotationHitTest.Contains(line, new PdfPoint(50, 60), 3).ShouldBeFalse();
    }

    [Fact]
    public void OpenShapes_ArePickedByTheirOutline_FilledOnesByTheirArea()
    {
        var box = new PdfRect(100, 100, 200, 160);
        var empty = new ShapeAnnotation("s", Red, 1, "", Created, Created, "", NoteShape.Square, box, 2, null);
        var filled = empty with { Fill = RgbColor.White };
        var ellipse = empty with { Shape = NoteShape.Circle };

        AnnotationHitTest.Contains(empty, new PdfPoint(150, 130), 2).ShouldBeFalse("what is inside stays reachable");
        AnnotationHitTest.Contains(empty, new PdfPoint(101, 130), 2).ShouldBeTrue();
        AnnotationHitTest.Contains(filled, new PdfPoint(150, 130), 2).ShouldBeTrue();
        AnnotationHitTest.Contains(ellipse, new PdfPoint(200, 130), 2).ShouldBeTrue("the right end of the ellipse");
        AnnotationHitTest.Contains(ellipse, new PdfPoint(102, 158), 2).ShouldBeFalse("the box's corner is off the ellipse");
    }

    [Fact]
    public void Polygons_ArePickedInside_PolylinesAlongTheirPath()
    {
        PdfPoint[] triangle = [new(0, 0), new(100, 0), new(50, 80)];
        var polygon = new PolyAnnotation("p", Red, 1, "", Created, Created, "", true, triangle, 1);
        var polyline = polygon with { Closed = false };

        AnnotationHitTest.Contains(polygon, new PdfPoint(50, 30), 1).ShouldBeTrue();
        AnnotationHitTest.Contains(polyline, new PdfPoint(50, 30), 1).ShouldBeFalse();
        AnnotationHitTest.Contains(polyline, new PdfPoint(50, 0.5), 1).ShouldBeTrue("on the first leg");
        AnnotationHitTest.Contains(polyline, new PdfPoint(75, 40), 1).ShouldBeTrue("on the second leg");
        AnnotationHitTest.Contains(polygon, new PdfPoint(25, 40), 1).ShouldBeTrue("on the side that closes the polygon");
        AnnotationHitTest.Contains(polyline, new PdfPoint(25, 40), 1).ShouldBeFalse("a polyline does not close");
    }

    [Fact]
    public void TextBoxes_ArePickedInTheirBox_OrOnTheirCallout()
    {
        var freeText = new FreeTextAnnotation("f", RgbColor.Black, 1, "", Created, Created, "Ghi chú", new PdfRect(200, 200, 300, 240), TextStyle.Default, null, null, [new PdfPoint(100, 100), new PdfPoint(200, 220)]);

        AnnotationHitTest.Contains(freeText, new PdfPoint(250, 220), 0).ShouldBeTrue();
        AnnotationHitTest.Contains(freeText, new PdfPoint(150, 160), 1).ShouldBeTrue("on the callout");
        AnnotationHitTest.Contains(freeText, new PdfPoint(150, 120), 1).ShouldBeFalse();
    }

    [Fact]
    public void At_ReturnsTheTopmost()
    {
        var under = new NoteAnnotation("under", Red, 1, "", Created, Created, "", new PdfPoint(100, 120));
        var over = new StampAnnotation("over", Red, 1, "", Created, Created, "", new PdfRect(90, 90, 200, 130), "ĐÃ DUYỆT");

        AnnotationHitTest.At([under, over], new PdfPoint(110, 110), 1).ShouldBeSameAs(over);
        AnnotationHitTest.At([over, under], new PdfPoint(110, 110), 1).ShouldBeSameAs(under);
        AnnotationHitTest.At([under, over], new PdfPoint(500, 500), 1).ShouldBeNull();
    }
}

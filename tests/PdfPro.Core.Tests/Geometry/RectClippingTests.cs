using PdfPro.Core.Geometry;

namespace PdfPro.Core.Tests.Geometry;

public sealed class RectClippingTests
{
    private static readonly PdfRect Hole = new(40, 40, 60, 60);

    [Fact]
    public void ALineThroughAnArea_KeepsBothEnds()
    {
        var pieces = RectClipping.Outside([new PdfPoint(0, 50), new PdfPoint(100, 50)], closed: false, [Hole]);

        pieces.Select(p => p.Points).ShouldBe(
        [
            [new PdfPoint(0, 50), new PdfPoint(40, 50)],
            [new PdfPoint(60, 50), new PdfPoint(100, 50)],
        ]);
        pieces.ShouldAllBe(p => !p.Closed);
    }

    [Fact]
    public void ALineOutsideOrAlongAnEdge_IsKeptWhole()
    {
        RectClipping.Outside([new PdfPoint(0, 10), new PdfPoint(100, 10)], closed: false, [Hole])
            .ShouldHaveSingleItem().Points.Count.ShouldBe(2);
        RectClipping.Outside([new PdfPoint(0, 40), new PdfPoint(100, 40)], closed: false, [Hole])
            .ShouldHaveSingleItem().Points.ShouldBe([new PdfPoint(0, 40), new PdfPoint(100, 40)], "touching the edge is not crossing it");
    }

    [Fact]
    public void ALineInsideAnArea_Goes()
        => RectClipping.Outside([new PdfPoint(45, 45), new PdfPoint(55, 55)], closed: false, [Hole]).ShouldBeEmpty();

    [Fact]
    public void APolyline_StaysJoinedBetweenCuts()
    {
        // Up through the hole, then right along the top, outside it.
        var pieces = RectClipping.Outside([new PdfPoint(50, 0), new PdfPoint(50, 100), new PdfPoint(100, 100)], closed: false, [Hole]);

        pieces.Select(p => p.Points).ShouldBe(
        [
            [new PdfPoint(50, 0), new PdfPoint(50, 40)],
            [new PdfPoint(50, 60), new PdfPoint(50, 100), new PdfPoint(100, 100)],
        ]);
    }

    [Fact]
    public void AClosedOutline_CutOnOneSide_OpensThere_AndRunsThroughItsFirstCorner()
    {
        // A square whose right side crosses the hole.
        var square = new[] { new PdfPoint(0, 0), new PdfPoint(50, 0), new PdfPoint(50, 100), new PdfPoint(0, 100) };

        var piece = RectClipping.Outside(square, closed: true, [Hole]).ShouldHaveSingleItem();

        piece.Closed.ShouldBeFalse();
        piece.Points.ShouldBe([new PdfPoint(50, 60), new PdfPoint(50, 100), new PdfPoint(0, 100), new PdfPoint(0, 0), new PdfPoint(50, 0), new PdfPoint(50, 40)]);
    }

    [Fact]
    public void AClosedOutline_NotCut_StaysClosed()
    {
        var square = new[] { new PdfPoint(0, 0), new PdfPoint(10, 0), new PdfPoint(10, 10), new PdfPoint(0, 10) };

        var piece = RectClipping.Outside(square, closed: true, [Hole]).ShouldHaveSingleItem();

        piece.Closed.ShouldBeTrue();
        piece.Points.ShouldBe(square);
    }

    [Fact]
    public void SeveralAreas_CutTheSameLine()
    {
        var pieces = RectClipping.Outside([new PdfPoint(0, 50), new PdfPoint(100, 50)], closed: false, [Hole, new PdfRect(80, 0, 90, 100)]);

        pieces.Select(p => (p.Points[0].X, p.Points[^1].X)).ShouldBe([(0.0, 40.0), (60.0, 80.0), (90.0, 100.0)]);
    }

    [Fact]
    public void ARectangleWithAHole_BecomesFourPiecesCoveringTheRest()
    {
        var pieces = RectClipping.Subtract(new PdfRect(0, 0, 100, 100), [Hole]);

        pieces.Count.ShouldBe(4);
        pieces.Sum(p => p.Width * p.Height).ShouldBe((100 * 100) - (20 * 20), 1e-9);
        pieces.ShouldAllBe(p => !RectClipping.Overlaps(p, Hole));
        for (var i = 0; i < pieces.Count; i++)
        {
            for (var j = i + 1; j < pieces.Count; j++)
            {
                RectClipping.Overlaps(pieces[i], pieces[j]).ShouldBeFalse();
            }
        }
    }

    [Fact]
    public void ARectangleCutAtAnEdge_KeepsTheRest_AndOneUnderAnArea_Goes()
    {
        RectClipping.Subtract(new PdfRect(50, 0, 100, 100), [Hole]).Sum(p => p.Width * p.Height).ShouldBe((50 * 100) - (10 * 20), 1e-9);
        RectClipping.Subtract(new PdfRect(45, 45, 55, 55), [Hole]).ShouldBeEmpty();
        RectClipping.Subtract(new PdfRect(0, 0, 10, 10), [Hole]).ShouldBe([new PdfRect(0, 0, 10, 10)]);
    }
}

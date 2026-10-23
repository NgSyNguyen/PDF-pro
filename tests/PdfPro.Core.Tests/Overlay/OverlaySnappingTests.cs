using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;

namespace PdfPro.Core.Tests.Overlay;

public sealed class OverlaySnappingTests
{
    private static readonly ViewRect Page = new(0, 0, 600, 800);

    [Fact]
    public void AnObjectNearThePageEdge_SnapsToIt_WithAGuide()
    {
        var result = OverlaySnapping.SnapMove(new ViewRect(4, 300, 100, 50), Page, []);

        result.DeltaX.ShouldBe(-4);
        result.DeltaY.ShouldBe(0);
        var guide = result.Guides.ShouldHaveSingleItem();
        guide.IsVertical.ShouldBeTrue();
        guide.Position.ShouldBe(0);
        (guide.From, guide.To).ShouldBe((0, 800));
    }

    [Fact]
    public void CentresSnapToThePageCentre()
    {
        var result = OverlaySnapping.SnapMove(new ViewRect(252, 375, 100, 50), Page, []);

        result.DeltaX.ShouldBe(-2, "the centre 302 snaps to 300");
        result.DeltaY.ShouldBe(0, "the middle is already at 400");
        result.Guides.Count.ShouldBe(2);
    }

    [Fact]
    public void ObjectsSnapToEachOther_AndTheClosestLineWins()
    {
        var other = new ViewRect(200, 100, 80, 40);

        var result = OverlaySnapping.SnapMove(new ViewRect(283, 500, 50, 20), Page, [other]);

        result.DeltaX.ShouldBe(-3, "left edge 283 meets the other's right edge 280");
        var guide = result.Guides.ShouldHaveSingleItem();
        (guide.From, guide.To).ShouldBe((100, 520), "the guide joins both objects");
    }

    [Fact]
    public void BeyondTheThreshold_NothingSnaps()
    {
        var result = OverlaySnapping.SnapMove(new ViewRect(40, 40, 100, 50), Page, [], threshold: 6);
        result.DeltaX.ShouldBe(0);
        result.Guides.ShouldBeEmpty();
    }

    [Fact]
    public void ADraggedEdge_Snaps()
    {
        var (value, guide) = OverlaySnapping.SnapEdge(596, isVertical: true, Page, []);

        value.ShouldBe(600);
        guide.ShouldNotBeNull().Position.ShouldBe(600);
        OverlaySnapping.SnapEdge(550, isVertical: true, Page, []).Guide.ShouldBeNull();
    }
}

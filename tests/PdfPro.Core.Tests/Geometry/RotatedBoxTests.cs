using PdfPro.Core.Geometry;

namespace PdfPro.Core.Tests.Geometry;

public sealed class RotatedBoxTests
{
    private static readonly RotatedBox Upright = new(new ViewPoint(100, 50), 40, 20, 0);

    [Fact]
    public void Corners_AndBounds_OfAnUprightBox()
    {
        Upright.Corners().ShouldBe([new ViewPoint(80, 40), new ViewPoint(120, 40), new ViewPoint(120, 60), new ViewPoint(80, 60)]);
        Upright.Bounds.ShouldBe(new ViewRect(80, 40, 40, 20));
    }

    [Fact]
    public void AQuarterTurn_SwapsTheBounds_AndTurnsClockwiseOnScreen()
    {
        var turned = Upright with { Angle = 90 };

        turned.Bounds.ShouldBe(new ViewRect(90, 30, 20, 40));
        turned.Corners()[0].ShouldBe(new ViewPoint(110, 30), "the top-left corner goes to the top-right");
    }

    [Fact]
    public void Contains_FollowsTheRotation()
    {
        var turned = Upright with { Angle = 45 };

        Upright.Contains(new ViewPoint(118, 42)).ShouldBeTrue();
        turned.Contains(new ViewPoint(118, 42)).ShouldBeFalse("the corner is outside once turned");
        turned.Contains(new ViewPoint(100, 50)).ShouldBeTrue();
        Upright.Contains(new ViewPoint(122, 50), tolerance: 3).ShouldBeTrue();
    }

    [Fact]
    public void Handles_AreAtTheCornersEdgesAndAboveTheTop()
    {
        Upright.HandlePosition(BoxHandle.Right).ShouldBe(new ViewPoint(120, 50));
        Upright.HandlePosition(BoxHandle.BottomLeft).ShouldBe(new ViewPoint(80, 60));
        Upright.HandlePosition(BoxHandle.Rotate, rotateOffset: 10).ShouldBe(new ViewPoint(100, 30));
        Upright.HitHandle(new ViewPoint(121, 41), radius: 3).ShouldBe(BoxHandle.TopRight);
        Upright.HitHandle(new ViewPoint(100, 50), radius: 3).ShouldBeNull();
    }

    [Fact]
    public void ResizingACorner_KeepsTheOppositeCorner()
    {
        var resized = Upright.Resize(BoxHandle.BottomRight, new ViewPoint(140, 70), keepAspect: false);

        resized.Width.ShouldBe(60, 1e-9);
        resized.Height.ShouldBe(30, 1e-9);
        resized.Corners()[0].X.ShouldBe(80, 1e-9);
        resized.Corners()[0].Y.ShouldBe(40, 1e-9);
    }

    [Fact]
    public void ResizingAnEdge_ChangesOneSideOnly()
    {
        var resized = Upright.Resize(BoxHandle.Left, new ViewPoint(60, 999), keepAspect: false);

        resized.Width.ShouldBe(60, 1e-9);
        resized.Height.ShouldBe(20);
        resized.Bounds.Right.ShouldBe(120, 1e-9);
        resized.Center.Y.ShouldBe(50, 1e-9);
    }

    [Fact]
    public void KeepingTheAspect_ScalesBothSides()
    {
        var resized = Upright.Resize(BoxHandle.BottomRight, new ViewPoint(160, 62), keepAspect: true);

        resized.Width.ShouldBe(80, 1e-9);
        resized.Height.ShouldBe(40, 1e-9);
        resized.Corners()[0].X.ShouldBe(80, 1e-9);
    }

    [Fact]
    public void ResizingATurnedBox_WorksAlongItsOwnAxes()
    {
        var turned = Upright with { Angle = 90 };
        var anchor = turned.HandlePosition(BoxHandle.TopLeft);

        // Dragging the bottom-right grip 10 units further along the box's own x axis (screen y, downwards).
        var grip = turned.HandlePosition(BoxHandle.BottomRight);
        var resized = turned.Resize(BoxHandle.BottomRight, new ViewPoint(grip.X, grip.Y + 10), keepAspect: false);

        resized.Width.ShouldBe(50, 1e-9);
        resized.Height.ShouldBe(20, 1e-9);
        resized.HandlePosition(BoxHandle.TopLeft).X.ShouldBe(anchor.X, 1e-9);
        resized.HandlePosition(BoxHandle.TopLeft).Y.ShouldBe(anchor.Y, 1e-9);
    }

    [Fact]
    public void ABoxCannotBeDraggedBelowTheMinimumSize()
    {
        var resized = Upright.Resize(BoxHandle.Right, new ViewPoint(10, 50), keepAspect: false, minSize: 4);

        resized.Width.ShouldBe(4);
        resized.Bounds.X.ShouldBe(80, 1e-9);
    }

    [Fact]
    public void Rotating_PointsTheGripAtThePointer_AndSnapsWithShift()
    {
        Upright.RotateTowards(new ViewPoint(200, 50), snap: false).Angle.ShouldBe(90, 1e-9);
        Upright.RotateTowards(new ViewPoint(100, 0), snap: false).Angle.ShouldBe(0, 1e-9);
        Upright.RotateTowards(new ViewPoint(0, 50), snap: false).Angle.ShouldBe(270, 1e-9);

        var free = Upright.RotateTowards(new ViewPoint(130, 0), snap: false).Angle;
        var snapped = Upright.RotateTowards(new ViewPoint(130, 0), snap: true).Angle;
        free.ShouldBe(30.96, 0.01);
        snapped.ShouldBe(30);
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(360, 0)]
    [InlineData(725, 5)]
    public void Angles_AreNormalized(double angle, double expected)
        => RotatedBox.NormalizeAngle(angle).ShouldBe(expected, 1e-9);
}

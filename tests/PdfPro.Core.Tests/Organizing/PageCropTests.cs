using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;

namespace PdfPro.Core.Tests.Organizing;

public sealed class PageCropTests
{
    private static readonly PdfSize Page = new(200, 300);

    [Theory]
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, -0.5, 10, 10)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, -1)]
    [InlineData(double.NaN, 0, 10, 10)]
    [InlineData(0, 0, double.PositiveInfinity, 10)]
    public void Constructor_RejectsInvalidRectangles(double left, double top, double width, double height)
        => Should.Throw<ArgumentOutOfRangeException>(() => new PageCrop(left, top, width, height));

    [Fact]
    public void Properties_DescribeTheRectangle()
    {
        var crop = new PageCrop(10, 20, 50, 80);

        crop.Right.ShouldBe(60);
        crop.Bottom.ShouldBe(100);
        crop.Size.ShouldBe(new PdfSize(50, 80));
    }

    [Theory]
    [InlineData(0, 10, 20, 50, 80)]
    [InlineData(1, 200, 10, 80, 50)]
    [InlineData(2, 140, 200, 50, 80)]
    [InlineData(3, 20, 140, 80, 50)]
    [InlineData(4, 10, 20, 50, 80)]
    [InlineData(-1, 20, 140, 80, 50)]
    public void Rotate_FollowsThePageClockwise(int quarterTurns, double left, double top, double width, double height)
        => new PageCrop(10, 20, 50, 80).Rotate(Page, quarterTurns).ShouldBe(new PageCrop(left, top, width, height));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rotate_ThenRotateBack_IsTheOriginal(int quarterTurns)
    {
        var crop = new PageCrop(12.5, 7.25, 101.5, 33);
        var rotatedPage = quarterTurns % 2 == 1 ? new PdfSize(Page.Height, Page.Width) : Page;

        var back = crop.Rotate(Page, quarterTurns).Rotate(rotatedPage, -quarterTurns);

        back.Left.ShouldBe(crop.Left, 1e-9);
        back.Top.ShouldBe(crop.Top, 1e-9);
        back.Width.ShouldBe(crop.Width, 1e-9);
        back.Height.ShouldBe(crop.Height, 1e-9);
    }

    [Fact]
    public void Rotate_CropTouchingTheEdge_StaysValid()
    {
        var crop = new PageCrop(0.1 + 0.2, 0, 200 - 0.3, 300);

        Should.NotThrow(() => crop.Rotate(Page, 2));
    }

    [Fact]
    public void CoversWholePage_AllowsTinyRoundingErrors()
    {
        new PageCrop(0.005, 0, 199.999, 300).CoversWholePage(Page).ShouldBeTrue();
        new PageCrop(1, 0, 199, 300).CoversWholePage(Page).ShouldBeFalse();
    }

    [Fact]
    public void Apply_WithoutRotation_CutsTheMargins()
        => PageCrop.Apply(Page, 0, null, new PageMargins(10, 20, 30, 40)).ShouldBe(new PageCrop(10, 20, 160, 240));

    [Fact]
    public void Apply_OnARotatedPage_ReadsMarginsAsTheUserSeesThem()
    {
        // Turned 90° clockwise, the page's left edge is on top and its bottom edge on the left.
        var crop = PageCrop.Apply(Page, 90, null, new PageMargins(left: 10, top: 20, right: 30, bottom: 40));

        crop.ShouldBe(new PageCrop(left: 20, top: 30, width: 140, height: 260));
        var shown = crop!.Value.Rotate(Page, 1);
        shown.Left.ShouldBe(10);
        shown.Top.ShouldBe(20);
        (300 - shown.Right).ShouldBe(30);
        (200 - shown.Bottom).ShouldBe(40);
    }

    [Fact]
    public void Apply_ToACroppedPage_CutsFromWhatIsVisible()
        => PageCrop.Apply(Page, 0, new PageCrop(10, 20, 160, 240), PageMargins.Uniform(10)).ShouldBe(new PageCrop(20, 30, 140, 220));

    [Fact]
    public void Apply_ZeroMarginsOnTheWholePage_RemovesTheCrop()
        => PageCrop.Apply(Page, 270, null, PageMargins.Uniform(0)).ShouldBeNull();

    [Fact]
    public void Apply_MarginsLeavingTooLittle_Throw()
    {
        var margins = new PageMargins(100, 0, 90, 0);

        PageCrop.Fits(Page, 0, null, margins).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => PageCrop.Apply(Page, 0, null, margins));
        PageCrop.Fits(Page, 0, null, new PageMargins(91, 0, 91, 0)).ShouldBeTrue();
    }

    [Fact]
    public void Apply_RejectsInvalidRotationAndPageSize()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PageCrop.Apply(Page, 45, null, PageMargins.Uniform(1)));
        Should.Throw<ArgumentOutOfRangeException>(() => PageCrop.Apply(new PdfSize(0, 10), 0, null, PageMargins.Uniform(1)));
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, double.NaN, 0, 0)]
    [InlineData(0, 0, double.PositiveInfinity, 0)]
    public void Margins_RejectNegativeOrNonFiniteValues(double left, double top, double right, double bottom)
        => Should.Throw<ArgumentOutOfRangeException>(() => new PageMargins(left, top, right, bottom));

    [Fact]
    public void Margins_IsZero()
    {
        PageMargins.Uniform(0).IsZero.ShouldBeTrue();
        new PageMargins(0, 0, 0, 0.1).IsZero.ShouldBeFalse();
    }
}

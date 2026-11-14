using PdfPro.Core.Geometry;
using PdfPro.Operations.Signing;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Signing;

/// <summary>The corner a batch signature goes in, on pages of any size and turn.</summary>
public sealed class SignatureCornerTests
{
    [Fact]
    public void TheBottomRightCorner_OfAnUprightPage()
    {
        var box = SignatureCorner.Place(new PageGeometry(new PdfRect(0, 0, 595, 842), 0));

        Round(box).ShouldBe(new PdfRect(595 - 36 - 180, 36, 595 - 36, 36 + 60));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void OnATurnedPage_ItIsTheCornerAsShown(int rotation)
    {
        var geometry = new PageGeometry(new PdfRect(0, 0, 595, 842), rotation);

        var box = SignatureCorner.Place(geometry);

        var shown = new PageCoordinateMapper(geometry, 1).PdfRectToScreen(box);
        var display = geometry.DisplaySize;
        Math.Round(shown.X + shown.Width).ShouldBe(Math.Round(display.Width - 36));
        Math.Round(shown.Y + shown.Height).ShouldBe(Math.Round(display.Height - 36));
        Math.Round(shown.Width).ShouldBe(180, "it reads upright, as wide as usual");
        Math.Round(shown.Height).ShouldBe(60);
    }

    [Fact]
    public void ATinyPage_GetsASmallerBox_InsideIt()
    {
        var box = SignatureCorner.Place(new PageGeometry(new PdfRect(0, 0, 100, 50), 0));

        box.Left.ShouldBeGreaterThanOrEqualTo(0);
        box.Bottom.ShouldBeGreaterThanOrEqualTo(0);
        box.Right.ShouldBeLessThanOrEqualTo(100);
        box.Top.ShouldBeLessThanOrEqualTo(50);
        box.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void TheFirstOrLastPage_OfAFile_InItsVisibleBox()
    {
        var file = File.ReadAllBytes(Samples.RotatedCropBox);

        var (first, firstBox) = SignatureCorner.Place(file, SignaturePage.First);
        var (last, lastBox) = SignatureCorner.Place(file, SignaturePage.Last);

        first.ShouldBe(1);
        last.ShouldBe(4);
        Round(firstBox).ShouldBe(new PdfRect(559 - 36 - 180, 72 + 36, 559 - 36, 72 + 36 + 60), "inside the crop box of page 1");
        lastBox.IsEmpty.ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => SignatureCorner.Place([1, 2, 3], SignaturePage.First));
    }

    private static PdfRect Round(PdfRect rect) => new(Math.Round(rect.Left), Math.Round(rect.Bottom), Math.Round(rect.Right), Math.Round(rect.Top));
}

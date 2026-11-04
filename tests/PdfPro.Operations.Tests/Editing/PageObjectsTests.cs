using PdfPro.Core.Editing;
using PdfPro.Core.Text;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Editing;

/// <summary>The drawings of a page as PDFium lists them for editing, on samples/.</summary>
public sealed class PageObjectsTests(PdfiumFixture pdfium)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TextObjects_HaveTheirWords_Font_AndPlace()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        var objects = await document.GetPageObjectsAsync(0, cancellationToken: Token);
        var size = document.PageSizes[0];

        objects.ShouldNotBeEmpty();
        objects.Select(o => o.Index).ShouldBe(objects.Select(o => o.Index).Order(), "in drawing order");
        var title = objects.Where(o => o.Kind == PageObjectKind.Text)
            .First(o => TextNormalizer.ToNfc(o.Text!.Text).Contains("CỘNG HÒA", StringComparison.Ordinal));
        title.Text!.IsEmbedded.ShouldBeTrue();
        title.Text.FontName.ShouldNotBeNullOrWhiteSpace();
        title.Text.SizeOnPage(title.Matrix).ShouldBeInRange(6, 40);
        title.Text.IsInvisible.ShouldBeFalse();
        title.Bounds.Top.ShouldBeGreaterThan(size.Height * 0.8, "the national motto is at the top");
        title.Bounds.Left.ShouldBeGreaterThanOrEqualTo(0);
        title.Bounds.Right.ShouldBeLessThanOrEqualTo(size.Width);
        title.Corners.Count.ShouldBe(4);
        title.Fill.ShouldNotBeNull();
    }

    [Fact]
    public async Task ImagesAndPaths_AreListed()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.Big1000);
        var objects = await document.GetPageObjectsAsync(0, cancellationToken: Token);

        var image = objects.First(o => o.Kind == PageObjectKind.Image);
        image.Image.ShouldNotBeNull();
        image.Image.PixelWidth.ShouldBeGreaterThan(0);
        image.Image.PixelHeight.ShouldBeGreaterThan(0);
        image.Bounds.Width.ShouldBeGreaterThan(1);
        objects.ShouldContain(o => o.Kind == PageObjectKind.Path, "the page has vector drawings");
        objects.ShouldContain(o => o.Kind == PageObjectKind.Text);
    }
}

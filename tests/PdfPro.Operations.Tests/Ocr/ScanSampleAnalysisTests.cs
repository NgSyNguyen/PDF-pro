using PdfPro.Core.Imaging;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Ocr;

/// <summary>
/// The tilt of the pages of samples/scan-vi.pdf — JPEG scans at 90 DPI whose 8×8 blocks and staircases sit square to the
/// picture — measured on PDFium's 300 DPI rendering. The true angles are those the sample generator turned the pages by
/// (its random seed), in the detector's convention: rising to the right is positive.
/// </summary>
public sealed class ScanSampleAnalysisTests(PdfiumFixture pdfium)
{
    [Theory]
    [InlineData(2, 0.922)]
    [InlineData(3, 1.093)]
    [InlineData(8, -1.145)]
    [InlineData(12, 0.359)]
    [InlineData(16, 0.575)]
    [InlineData(17, -0.378)]
    public async Task TheTilt_OfAScannedPage_IsMeasured_DespiteJpegBlocks(int page, double degrees)
    {
        var image = await RenderAsync(page);

        var estimate = SkewDetector.Estimate(image).ShouldNotBeNull();

        estimate.Degrees.ShouldBe(degrees, 0.2);
        estimate.IsReliable.ShouldBeTrue();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public async Task ASeparatorSheet_HasNoTilt_AndNoInk(int page)
    {
        var image = await RenderAsync(page);

        (SkewDetector.Estimate(image)?.IsReliable ?? false).ShouldBeFalse();
        InkCoverage.DarkPercent(GrayTransforms.ShrinkToWidth(image, 446).Image).ShouldBeLessThan(0.1);
    }

    private Task<GrayImage> RenderAsync(int page)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, Samples.ScanVi);
                using var bitmap = document.RenderPage(page - 1, new PageRenderOptions { Scale = 300.0 / 72, Flags = PdfRenderFlags.None });
                return GrayImage.FromBgra(bitmap.Width, bitmap.Height, bitmap.Pixels, bitmap.Stride);
            },
            PdfiumPriority.Normal,
            TestContext.Current.CancellationToken);
}

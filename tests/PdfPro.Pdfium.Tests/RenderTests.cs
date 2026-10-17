using System.Diagnostics;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium.Tests.TestSupport;

namespace PdfPro.Pdfium.Tests;

public sealed class RenderTests(PdfiumGateFixture fixture)
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstPage_MatchesTheGoldenImage()
    {
        await using var document = await Open(Samples.BasicVi);

        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1.0 }, cancellationToken: TestToken);

        bitmap.Width.ShouldBe(595);
        bitmap.Height.ShouldBe(842);
        GoldenImages.AssertMatches(bitmap, "basic-vi-page1.png");
    }

    [Fact]
    public async Task Background_IsWhite()
    {
        await using var document = await Open(Samples.BasicVi);

        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 0.5 }, cancellationToken: TestToken);

        bitmap.GetPixel(2, 2).ShouldBe(((byte)255, (byte)255, (byte)255));
        bitmap.GetPixel(bitmap.Width - 3, bitmap.Height - 3).ShouldBe(((byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public async Task Size_FollowsTheScale()
    {
        await using var document = await Open(Samples.BasicVi);

        using var bitmap = await document.RenderPageAsync(1, new PageRenderOptions { Scale = 2.0 }, cancellationToken: TestToken);

        (bitmap.Width, bitmap.Height).ShouldBe((1190, 1684));
        bitmap.Stride.ShouldBeGreaterThanOrEqualTo(bitmap.Width * 4);
        bitmap.Region.ShouldBe(new PixelRect(0, 0, 1190, 1684));
        bitmap.ByteSize.ShouldBe((long)bitmap.Stride * bitmap.Height);
    }

    [Fact]
    public async Task Region_EqualsTheSamePartOfTheFullRender()
    {
        await using var document = await Open(Samples.BasicVi);
        var region = new PixelRect(150, 200, 300, 180);

        using var full = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1.5 }, cancellationToken: TestToken);
        using var part = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1.5, Region = region }, cancellationToken: TestToken);

        (part.Width, part.Height).ShouldBe((region.Width, region.Height));
        (part.PagePixelWidth, part.PagePixelHeight).ShouldBe((full.Width, full.Height));
        var different = 0;
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                var a = part.GetPixel(x, y);
                var b = full.GetPixel(region.X + x, region.Y + y);
                if (Math.Abs(a.R - b.R) > 8 || Math.Abs(a.G - b.G) > 8 || Math.Abs(a.B - b.B) > 8)
                {
                    different++;
                }
            }
        }

        different.ShouldBeLessThan(region.Width * region.Height / 200, "a region render must line up with the full render");
    }

    /// <summary>
    /// rotated-cropbox.pdf draws a red square at the top-left of each page's visible box (in page space).
    /// After /Rotate the square must appear at the corresponding corner of the displayed page.
    /// </summary>
    [Theory]
    [InlineData(0, "top-left")]
    [InlineData(1, "top-right")]
    [InlineData(2, "bottom-right")]
    [InlineData(3, "bottom-left")]
    public async Task Rotation_MovesThePageTopLeftMarker(int pageIndex, string expectedCorner)
    {
        await using var document = await Open(Samples.RotatedCropBox);

        using var bitmap = await document.RenderPageAsync(pageIndex, new PageRenderOptions { Scale = 1.0 }, cancellationToken: TestToken);

        var (x, y) = expectedCorner switch
        {
            "top-left" => (28, 28),
            "top-right" => (bitmap.Width - 28, 28),
            "bottom-right" => (bitmap.Width - 28, bitmap.Height - 28),
            _ => (28, bitmap.Height - 28),
        };
        IsRed(bitmap.GetPixel(x, y)).ShouldBeTrue($"page {pageIndex}: red marker expected at the {expectedCorner} corner");
        IsLightYellow(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2)).ShouldBeTrue("the visible box fills the page");
    }

    [Fact]
    public async Task ExtraQuarterTurn_RotatesTheOutputClockwise()
    {
        await using var document = await Open(Samples.RotatedCropBox);

        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1.0, ExtraQuarterTurns = 1 }, cancellationToken: TestToken);

        (bitmap.Width, bitmap.Height).ShouldBe((698, 523));
        IsRed(bitmap.GetPixel(bitmap.Width - 28, 28)).ShouldBeTrue("top-left moves to top-right after a clockwise quarter turn");
    }

    [Fact]
    public async Task Grayscale_RemovesColor()
    {
        await using var document = await Open(Samples.RotatedCropBox);

        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1.0, Flags = PdfRenderFlags.Grayscale }, cancellationToken: TestToken);

        var (b, g, r) = bitmap.GetPixel(28, 28);
        (r == g && g == b).ShouldBeTrue($"expected a gray pixel, got R{r} G{g} B{b}");
    }

    [Fact]
    public async Task InvalidRequests_AreRejected()
    {
        await using var document = await Open(Samples.BasicVi);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => document.RenderPageAsync(0, new PageRenderOptions { Scale = 0 }, cancellationToken: TestToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => document.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Region = new PixelRect(500, 800, 200, 200) }, cancellationToken: TestToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => document.RenderPageAsync(0, new PageRenderOptions { Scale = 20 }, cancellationToken: TestToken));
    }

    [Fact]
    public async Task BigDocument_FirstPageWithinBudget()
    {
        var stopwatch = Stopwatch.StartNew();

        await using var document = await Open(Samples.Big1000);
        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1.5 }, cancellationToken: TestToken);

        stopwatch.Stop();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "open + first page must stay under 2 s");
        bitmap.Width.ShouldBe(893);
    }

    [Fact]
    public async Task ConcurrentRequests_AreSerializedSafely()
    {
        await using var document = await Open(Samples.Big1000);

        var renders = Enumerable.Range(0, 40).Select(i => Task.Run(
            async () =>
            {
                using var bitmap = await document.RenderPageAsync(i * 25, new PageRenderOptions { Scale = 0.25 }, PdfiumPriority.Normal, TestToken);
                return bitmap.Width;
            },
            TestToken));

        (await Task.WhenAll(renders)).ShouldAllBe(width => width == 149);
    }

    private static bool IsRed((byte B, byte G, byte R) pixel) => pixel.R > 200 && pixel.G < 60 && pixel.B < 60;

    private static bool IsLightYellow((byte B, byte G, byte R) pixel) => pixel.R > 240 && pixel.G > 240 && pixel.B is > 190 and < 235;

    private Task<PdfDocumentHandle> Open(string path) => PdfDocumentHandle.OpenAsync(fixture.Gate, path, cancellationToken: TestToken);
}

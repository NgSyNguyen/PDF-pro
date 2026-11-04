using PdfPro.Core.Editing;
using PdfPro.Core.Imaging;
using PdfPro.Operations.Editing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Editing;

/// <summary>Images of a page: taken out as stored, replaced keeping their frame, turned.</summary>
public sealed class ImageEditTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly EditContentOperation _operation = new(pdfium.Gate);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AnImage_IsTakenOut_AtTheResolutionItIsStoredIn()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.Big1000);
        var image = (await document.GetPageObjectsAsync(0, cancellationToken: Token)).First(o => o.Kind == PageObjectKind.Image);

        var extracted = await document.ExtractImageAsync(0, image, Token);

        (extracted.PixelWidth, extracted.PixelHeight).ShouldBe((image.Image!.PixelWidth, image.Image.PixelHeight));
        extracted.Extension.ShouldBeOneOf(".jpg", ".png", ".jp2");
        if (extracted.Extension == ".png")
        {
            var pixels = PngCodec.Decode(extracted.Data);
            (pixels.Width, pixels.Height).ShouldBe((image.Image.PixelWidth, image.Image.PixelHeight));
        }
        else if (extracted.Extension == ".jpg")
        {
            extracted.Data.Take(2).ShouldBe(new byte[] { 0xFF, 0xD8 });
        }
    }

    [Fact]
    public async Task APicture_TakesTheImagesFrame_KeepingItsShape_AndOtherPagesKeepTheOldImage()
    {
        var (page, image) = await ImageOfAPageAsync(0);
        var picture = _temp.Combine("red.png");
        await File.WriteAllBytesAsync(picture, PngCodec.Encode(Solid(40, 10, red: 255)), Token);
        var shared = await SharedImagePageAsync(image);

        var output = await ApplyAsync(new ReplaceImageEdit(PageObjectRef.Of(1, image), picture));

        await using var edited = await PdfInspector.OpenAsync(pdfium.Gate, output);
        var objects = await edited.GetPageObjectsAsync(page, cancellationToken: Token);
        var replaced = objects.Single(o => o.Kind == PageObjectKind.Image);
        (replaced.Image!.PixelWidth, replaced.Image.PixelHeight).ShouldBe((40, 10));
        replaced.Bounds.Left.ShouldBeGreaterThanOrEqualTo(image.Bounds.Left - 0.5);
        replaced.Bounds.Right.ShouldBeLessThanOrEqualTo(image.Bounds.Right + 0.5);
        replaced.Bounds.Bottom.ShouldBeGreaterThanOrEqualTo(image.Bounds.Bottom - 0.5);
        replaced.Bounds.Top.ShouldBeLessThanOrEqualTo(image.Bounds.Top + 0.5);
        (replaced.Bounds.Width / replaced.Bounds.Height).ShouldBe(4, 0.05, "the picture's shape is kept");
        using var bitmap = await edited.RenderPageAsync(page, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        var centre = bitmap.GetPixel((int)((replaced.Bounds.Left + replaced.Bounds.Right) / 2), bitmap.Height - (int)((replaced.Bounds.Bottom + replaced.Bounds.Top) / 2));
        (centre.R > 200 && centre.G < 60 && centre.B < 60).ShouldBeTrue($"the new picture is shown ({centre})");
        if (shared is { } other)
        {
            var kept = (await edited.GetPageObjectsAsync(other.Page, cancellationToken: Token)).First(o => o.Kind == PageObjectKind.Image);
            (kept.Image!.PixelWidth, kept.Image.PixelHeight).ShouldBe((image.Image!.PixelWidth, image.Image.PixelHeight), "another page showing the same image keeps it");
        }
    }

    [Fact]
    public async Task AJpeg_IsTakenOutAndWrittenAsItIs()
    {
        // The scanned sample stores its pages as JPEG (DCTDecode).
        await using var scan = await PdfInspector.OpenAsync(pdfium.Gate, Samples.ScanVi);
        var photo = (await scan.GetPageObjectsAsync(0, cancellationToken: Token)).First(o => o.Kind == PageObjectKind.Image);
        var extracted = await scan.ExtractImageAsync(0, photo, Token);
        extracted.Extension.ShouldBe(".jpg");
        extracted.Data.Take(2).ShouldBe(new byte[] { 0xFF, 0xD8 });
        (extracted.PixelWidth, extracted.PixelHeight).ShouldBe((photo.Image!.PixelWidth, photo.Image.PixelHeight));
        var picture = _temp.Combine("photo.jpg");
        await File.WriteAllBytesAsync(picture, extracted.Data, Token);
        var (_, image) = await ImageOfAPageAsync(0);

        var output = await ApplyAsync(new ReplaceImageEdit(PageObjectRef.Of(1, image), picture, KeepAspect: false));

        await using var edited = await PdfInspector.OpenAsync(pdfium.Gate, output);
        var replaced = (await edited.GetPageObjectsAsync(0, cancellationToken: Token)).Single(o => o.Kind == PageObjectKind.Image);
        (replaced.Image!.PixelWidth, replaced.Image.PixelHeight).ShouldBe((photo.Image.PixelWidth, photo.Image.PixelHeight));
        var again = await edited.ExtractImageAsync(0, replaced, Token);
        again.Extension.ShouldBe(".jpg");
        again.Data.ShouldBe(extracted.Data, "the JPEG bytes are not encoded again");
        replaced.Bounds.Left.ShouldBe(image.Bounds.Left, 0.5);
        replaced.Bounds.Width.ShouldBe(image.Bounds.Width, 0.5, "stretched to the frame");
    }

    [Fact]
    public async Task Turning_KeepsTheCentre()
    {
        var (_, image) = await ImageOfAPageAsync(0);

        var output = await ApplyAsync(new RotateObjectEdit(PageObjectRef.Of(1, image), 90));

        await using var edited = await PdfInspector.OpenAsync(pdfium.Gate, output);
        var turned = (await edited.GetPageObjectsAsync(0, cancellationToken: Token)).Single(o => o.Kind == PageObjectKind.Image);
        turned.Bounds.Width.ShouldBe(image.Bounds.Height, 0.5);
        turned.Bounds.Height.ShouldBe(image.Bounds.Width, 0.5);
        ((turned.Bounds.Left + turned.Bounds.Right) / 2).ShouldBe((image.Bounds.Left + image.Bounds.Right) / 2, 0.5);
        ((turned.Bounds.Bottom + turned.Bounds.Top) / 2).ShouldBe((image.Bounds.Bottom + image.Bounds.Top) / 2, 0.5);
    }

    [Fact]
    public void Pictures_AreOnlyForImages()
    {
        var target = new PageObjectRef(1, 0, PageObjectKind.Path, new Core.Geometry.PdfRect(0, 0, 10, 10));
        var document = new Core.Documents.DocumentInfo(1);

        _operation.Validate(new EditContentParams([new ReplaceImageEdit(target, "a.png")]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditContentParams([new ReplaceImageEdit(target with { Kind = PageObjectKind.Image }, " ")]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditContentParams([new ReplaceImageEdit(target with { Kind = PageObjectKind.Image }, _temp.Combine("missing.png"))]), document).IsValid.ShouldBeFalse("the picture must exist");
        _operation.Validate(new EditContentParams([new RotateObjectEdit(target, 360)]), document).IsValid.ShouldBeFalse("a whole turn changes nothing");
        _operation.Validate(new EditContentParams([new RotateObjectEdit(target, -90)]), document).IsValid.ShouldBeTrue();
    }

    private static BgraImage Solid(int width, int height, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 2] = red;
            pixels[i + 3] = 255;
        }

        return new BgraImage(width, height, pixels);
    }

    private async Task<(int Page, PageObjectInfo Image)> ImageOfAPageAsync(int page)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.Big1000);
        return (page, (await document.GetPageObjectsAsync(page, cancellationToken: Token)).Single(o => o.Kind == PageObjectKind.Image));
    }

    /// <summary>Another page among the first few showing an image of the same pixel size (the sample shares 5 images), or null.</summary>
    private async Task<(int Page, PageObjectInfo Image)?> SharedImagePageAsync(PageObjectInfo image)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.Big1000);
        for (var page = 1; page < 10; page++)
        {
            var other = (await document.GetPageObjectsAsync(page, cancellationToken: Token)).FirstOrDefault(o => o.Kind == PageObjectKind.Image);
            if (other?.Image == image.Image)
            {
                return (page, other!);
            }
        }

        return null;
    }

    private async Task<string> ApplyAsync(params ContentEdit[] edits)
    {
        var output = _temp.Combine($"edited-{Guid.NewGuid():N}.pdf");
        var stream = File.Create(output);
        await using (stream)
        {
            await _operation.ApplyToFileAsync(Samples.Big1000, stream, new EditContentParams(edits), new RecordingProgress(), Token);
        }

        return output;
    }
}

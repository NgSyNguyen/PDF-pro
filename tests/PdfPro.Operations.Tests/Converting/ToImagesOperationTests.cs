using PdfPro.Core.Imaging;
using PdfPro.Core.Operations;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Tests.TestSupport;
using SkiaSharp;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// "PDF sang ảnh" (PROMPTS P9 C1): pages as PNG (transparent if asked), JPEG or TIFF, at the chosen resolution and
/// pages, named after the document, nothing left behind when cancelled.
/// </summary>
public sealed class ToImagesOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ChosenPages_BecomePngs_NamedByTheirPageNumber_AtTheResolution()
    {
        var folder = _temp.Combine("ảnh");

        var result = await Operation().WriteAsync(Samples.BasicVi, null, folder, "hồ sơ", new ToImagesParams(Dpi: 72, Pages: "2-3"), null, Token);

        result.PageCount.ShouldBe(2);
        result.Files.Select(Path.GetFileName).ShouldBe(["hồ sơ-002.png", "hồ sơ-003.png"]);
        result.ReducedPages.ShouldBeEmpty();
        using var bitmap = SKBitmap.Decode(result.Files[0]);
        ((double)bitmap.Width).ShouldBe(595, 1);
        ((double)bitmap.Height).ShouldBe(842, 1);
        bitmap.AlphaType.ShouldBe(SKAlphaType.Opaque);
        Directory.GetFiles(folder).Length.ShouldBe(2, "no temporary file is left");
        result.Bytes.ShouldBe(result.Files.Sum(f => new FileInfo(f).Length));
        ImageResolution.ReadPng(File.ReadAllBytes(result.Files[0])).ShouldNotBeNull().X.ShouldBe(72, 0.05, "the PNG says its resolution");
    }

    [Fact]
    public async Task ATransparentPng_LeavesTheUnpaintedPageClear_AndTheTextOpaque()
    {
        var result = await Operation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToImagesParams(Dpi: 72, Pages: "1", Transparent: true), null, Token);

        using var bitmap = SKBitmap.Decode(result.Files[0]);
        bitmap.GetPixel(2, 2).Alpha.ShouldBe((byte)0, "the margin is not painted");
        var opaque = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                opaque += bitmap.GetPixel(x, y).Alpha == 255 ? 1 : 0;
            }
        }

        opaque.ShouldBeGreaterThan(50, "the text is painted");
    }

    [Fact]
    public async Task Jpegs_HaveTheQualityAsked()
    {
        var good = await Operation().WriteAsync(Samples.BasicVi, null, _temp.Combine("good"), "a", new ToImagesParams(PageImageFormat.Jpeg, Dpi: 100, Pages: "1", JpegQuality: 95), null, Token);
        var poor = await Operation().WriteAsync(Samples.BasicVi, null, _temp.Combine("poor"), "a", new ToImagesParams(PageImageFormat.Jpeg, Dpi: 100, Pages: "1", JpegQuality: 30), null, Token);

        Path.GetFileName(good.Files[0]).ShouldBe("a-001.jpg");
        ((double)JpegHeader.Read(File.ReadAllBytes(good.Files[0])).ShouldNotBeNull().Quality!.Value).ShouldBe(95, 2);
        ((double)JpegHeader.Read(File.ReadAllBytes(poor.Files[0])).ShouldNotBeNull().Quality!.Value).ShouldBe(30, 2);
        poor.Bytes.ShouldBeLessThan(good.Bytes);
        ImageResolution.ReadJpeg(File.ReadAllBytes(good.Files[0])).ShouldBe((100.0, 100.0), "the JFIF header says the resolution");
    }

    [Theory]
    [InlineData(TiffColor.Rgb, 5)]
    [InlineData(TiffColor.Gray, 5)]
    [InlineData(TiffColor.BlackWhite, 4)]
    public async Task OneTiff_HoldsEveryPage(TiffColor color, int compression)
    {
        var result = await Operation().WriteAsync(Samples.BasicVi, null, _temp.Path, "hồ sơ", new ToImagesParams(PageImageFormat.Tiff, Dpi: 96, TiffColor: color), null, Token);

        result.Files.Select(Path.GetFileName).ShouldBe(["hồ sơ.tif"]);
        result.PageCount.ShouldBe(6);
        var directories = Directories(File.ReadAllBytes(result.Files[0]));
        directories.Count.ShouldBe(6);
        directories.ShouldAllBe(d => d[259] == compression);
    }

    [Fact]
    public async Task TiffsOnePerPage_AreSingleTiffs()
    {
        var result = await Operation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToImagesParams(PageImageFormat.Tiff, Dpi: 72, Pages: "odd", OneTiff: false), null, Token);

        result.Files.Select(Path.GetFileName).ShouldBe(["a-001.tif", "a-003.tif", "a-005.tif"]);
        result.Files.ShouldAllBe(f => Directories(File.ReadAllBytes(f)).Count == 1);
    }

    [Fact]
    public async Task FilesAlreadyThere_AreKept_UnlessOverwriting()
    {
        var existing = _temp.Combine("a-001.png");
        await File.WriteAllTextAsync(existing, "đã có", Token);

        var kept = await Operation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToImagesParams(Dpi: 36, Pages: "1"), null, Token);

        Path.GetFileName(kept.Files[0]).ShouldBe("a-001 (2).png");
        (await File.ReadAllTextAsync(existing, Token)).ShouldBe("đã có");

        var replaced = await Operation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToImagesParams(Dpi: 36, Pages: "1", Overwrite: true), null, Token);

        replaced.Files.ShouldBe([existing]);
        SKBitmap.Decode(existing).ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancelling_LeavesTheFolderAsItWas()
    {
        var folder = _temp.Combine("hủy");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new SynchronousProgress(p =>
        {
            if (p.Completed == 3)
            {
                cancel.Cancel();
            }
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Operation().WriteAsync(Samples.BasicVi, null, folder, "a", new ToImagesParams(Dpi: 36), progress, cancel.Token));

        Directory.GetFiles(folder).ShouldBeEmpty("pages written before the cancel are removed with their temporary names");
    }

    [Fact]
    public async Task APageRangeOutsideTheDocument_IsRefused()
    {
        var error = await Should.ThrowAsync<OperationValidationException>(() => Operation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToImagesParams(Pages: "7-9"), null, Token));

        error.Errors.ShouldContain(e => e.MemberName == nameof(ToImagesParams.Pages));
        Directory.GetFiles(_temp.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task APageTooLargeForTheResolution_IsWrittenSmaller_AndSaidSo()
    {
        var result = await Operation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToImagesParams(PageImageFormat.Jpeg, Dpi: 1200, Pages: "1", JpegQuality: 50), null, Token);

        result.ReducedPages.ShouldBe([0], "A4 at 1200 DPI is 139 megapixels");
        using var codec = SKCodec.Create(result.Files[0]);
        ((long)codec.Info.Width * codec.Info.Height).ShouldBeLessThanOrEqualTo(ToImagesOperation.MaxPixels);
    }

    [Fact]
    public async Task AProtectedFile_OpensWithItsPassword()
    {
        var result = await Operation().WriteAsync(Samples.Encrypted, "test123", _temp.Path, "mật", new ToImagesParams(Dpi: 36, Pages: "1"), null, Token);

        result.PageCount.ShouldBe(1);
        File.Exists(result.Files[0]).ShouldBeTrue();
    }

    private ToImagesOperation Operation() => new(pdfium.Gate);

    /// <summary>The tags of each directory of a little-endian TIFF, with their first value.</summary>
    private static List<Dictionary<int, long>> Directories(byte[] file)
    {
        var list = new List<Dictionary<int, long>>();
        var offset = BitConverter.ToUInt32(file, 4);
        while (offset != 0)
        {
            var count = BitConverter.ToUInt16(file, (int)offset);
            var tags = new Dictionary<int, long>();
            for (var i = 0; i < count; i++)
            {
                var at = (int)offset + 2 + (12 * i);
                var type = BitConverter.ToUInt16(file, at + 2);
                tags[BitConverter.ToUInt16(file, at)] = type == 3 ? BitConverter.ToUInt16(file, at + 8) : BitConverter.ToUInt32(file, at + 8);
            }

            list.Add(tags);
            offset = BitConverter.ToUInt32(file, (int)offset + 2 + (12 * count));
        }

        return list;
    }

    private sealed class SynchronousProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}

using System.Security.Cryptography;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Tests.Optimizing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Extracting;

/// <summary>Images taken out of pages as stored: JPEG byte for byte, others as PNG; inside forms too; small ones and repeats left out.</summary>
public sealed class ExtractImagesTests(PdfiumFixture pdfium)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheScans_AreWrittenByteForByte_OnePerPage()
    {
        using var temp = new TempDirectory();
        var progress = new RecordingProgress();

        var result = await new ExtractImagesOperation(pdfium.Gate).WriteAsync(Samples.ScanVi, null, temp.Path, "bản quét", new ExtractImagesParams(SkipDuplicates: false), progress, Token);

        result.Files.Count.ShouldBe(18);
        result.Files.ShouldAllBe(f => f.EndsWith(".jpg", StringComparison.Ordinal));
        Path.GetFileName(result.Files[0]).ShouldBe("bản quét-001-1.jpg");
        Path.GetFileName(result.Files[17]).ShouldBe("bản quét-018-1.jpg");
        var stored = ImagePdf.Images(ImagePdf.Read(Samples.ScanVi)).Select(i => Hash(i.Stream.Value)).ToList();
        result.Files.Select(f => Hash(File.ReadAllBytes(f))).ShouldBe(stored);
        Directory.GetFiles(temp.Path).Length.ShouldBe(18, "no temporary file is left");
        progress.Reports[^1].Completed.ShouldBe(18);
    }

    [Fact]
    public async Task ImagesInForms_ArePng_RepeatsAndSmallOnesAreLeftOut()
    {
        using var temp = new TempDirectory();
        var input = temp.Combine("in.pdf");
        using (var builder = new ImagePdf())
        {
            var photo = builder.Samples(ImagePdf.Photo(64, 48), 64, 48, 3);
            var icon = builder.Samples(ImagePdf.Photo(8, 8, seed: 5), 8, 8, 3);
            var form = builder.Form(PdfMatrix.Identity, photo, new PdfMatrix(64, 0, 0, 48, 0, 0));
            var first = builder.Page();
            builder.Draw(first, form, new PdfMatrix(1, 0, 0, 1, 100, 100));
            builder.Draw(first, icon, new PdfMatrix(8, 0, 0, 8, 10, 10));
            var second = builder.Page();
            builder.Draw(second, photo, new PdfMatrix(64, 0, 0, 48, 200, 200));
            builder.Save(input);
        }

        var output = temp.Combine("out");
        var result = await new ExtractImagesOperation(pdfium.Gate).WriteAsync(input, null, output, "ảnh", new ExtractImagesParams(MinSide: 16), null, Token);

        var file = result.Files.ShouldHaveSingleItem();
        Path.GetFileName(file).ShouldBe("ảnh-001-1.png");
        (result.Duplicates, result.TooSmall, result.Unreadable).ShouldBe((1, 1, 0));
        var png = PngCodec.Decode(File.ReadAllBytes(file));
        (png.Width, png.Height).ShouldBe((64, 48));
    }

    [Fact]
    public async Task ThePagesAsked_AreTheOnlyOnesRead_AndExistingFilesAreKept()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("x-002-1.jpg"), "mine");
        var operation = new ExtractImagesOperation(pdfium.Gate);

        var result = await operation.WriteAsync(Samples.ScanVi, null, temp.Path, "x", new ExtractImagesParams(Pages: "2-3"), null, Token);

        result.Files.Select(Path.GetFileName).ShouldBe(["x-002-1 (2).jpg", "x-003-1.jpg"]);
        File.ReadAllText(temp.Combine("x-002-1.jpg")).ShouldBe("mine");
        await Should.ThrowAsync<Core.Operations.OperationValidationException>(() => operation.WriteAsync(Samples.ScanVi, null, temp.Path, "x", new ExtractImagesParams(Pages: "40"), null, Token));
        await Should.ThrowAsync<PdfDocumentException>(() => operation.WriteAsync(Samples.Encrypted, null, temp.Path, "x", new ExtractImagesParams(), null, Token));
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}

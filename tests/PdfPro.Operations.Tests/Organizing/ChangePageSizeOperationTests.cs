using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Organizing;

/// <summary>Golden tests on the samples: page sizes, where the content lands, annotations, links and destinations.</summary>
public sealed class ChangePageSizeOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly PdfSize A3 = PaperSizes.SizeOf("A3")!.Value;
    private static readonly PdfSize A5 = PaperSizes.SizeOf("A5")!.Value;

    private readonly TempDirectory _temp = new();
    private readonly ChangePageSizeOperation _operation = new();

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAContentChange()
    {
        _operation.Id.ShouldBe("organize.pageSize");
        _operation.Kind.ShouldBe(OperationKind.Content);
        _operation.ParamsType.ShouldBe(typeof(ChangePageSizeParams));
    }

    [Theory]
    [InlineData(595, 842, true, 420, 595)] // portrait page, portrait size
    [InlineData(842, 595, true, 595, 420)] // landscape page: the size turns
    [InlineData(842, 595, false, 420, 595)] // unless asked not to
    [InlineData(500, 500, true, 420, 595)] // a square page takes the size as given
    public void TargetSize_FollowsThePagesOrientation(double width, double height, bool match, double expectedWidth, double expectedHeight)
        => ChangePageSizeOperation.TargetSize(new PdfSize(width, height), new ChangePageSizeParams("1", 420, 595, MatchOrientation: match))
            .ShouldBe(new PdfSize(expectedWidth, expectedHeight));

    [Theory]
    [InlineData("0", 595, 842, PageSizeFit.ScaleToFit)]
    [InlineData("1-9", 595, 842, PageSizeFit.ScaleToFit)]
    [InlineData("1", 2, 842, PageSizeFit.ScaleToFit)]
    [InlineData("1", 595, 20000, PageSizeFit.ScaleToFit)]
    [InlineData("1", 595, 842, (PageSizeFit)7)]
    public void InvalidParameters_AreRefused(string pages, double width, double height, PageSizeFit fit)
        => _operation.Validate(new ChangePageSizeParams(pages, width, height, fit), new DocumentInfo(6)).IsValid.ShouldBeFalse();

    [Fact]
    public async Task ScaleToFit_A4ToA3_ScalesTheChosenPagesAndKeepsTheirText()
    {
        var output = await ResizeAsync(Samples.BasicVi, new ChangePageSizeParams("1, 3", A3.Width, A3.Height));

        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await Sizes(output)).ShouldBe([A3, new PdfSize(595, 842), A3, new PdfSize(595, 842), new PdfSize(595, 842), new PdfSize(595, 842)]);
        (await PdfInspector.AllTextsAsync(after)).ShouldBe(await PdfInspector.AllTextsAsync(before));

        var scale = Math.Min(A3.Width / 595, A3.Height / 842);
        var original = await InkBoundsAsync(before, 0);
        var resized = await InkBoundsAsync(after, 0);
        var offsetX = (A3.Width - (595 * scale)) / 2;
        resized.Left.ShouldBe((original.Left * scale) + offsetX, 3);
        resized.Right.ShouldBe((original.Right * scale) + offsetX, 3);
        resized.Top.ShouldBe(original.Top * scale, 3);
        resized.Bottom.ShouldBe(original.Bottom * scale, 3);
        (await InkBoundsAsync(after, 1)).ShouldBe(await InkBoundsAsync(before, 1), "other pages are untouched");
    }

    [Fact]
    public async Task Center_A4ToA5_KeepsTheContentSizeCentredAndClipped()
    {
        var output = await ResizeAsync(Samples.BasicVi, new ChangePageSizeParams("2", A5.Width, A5.Height, PageSizeFit.Center));

        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await Sizes(output))[1].ShouldBe(A5);
        var original = await InkBoundsAsync(before, 1);
        var resized = await InkBoundsAsync(after, 1);
        var shiftX = (A5.Width - 595) / 2;
        var shiftY = (A5.Height - 842) / 2;
        resized.Left.ShouldBe(Math.Max(0, original.Left + shiftX), 3);
        resized.Top.ShouldBe(Math.Max(0, original.Top + shiftY), 3);
        resized.Right.ShouldBeLessThanOrEqualTo(A5.Width);
        resized.Bottom.ShouldBeLessThanOrEqualTo(A5.Height);
    }

    [Fact]
    public async Task RotatedAndCroppedPages_KeepTheirOrientationAndCorners()
    {
        var output = await ResizeAsync(Samples.RotatedCropBox, new ChangePageSizeParams("1-4", A5.Width, A5.Height));

        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.RotatedCropBox);
        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        var oldSizes = await Sizes(Samples.RotatedCropBox);
        var newSizes = await Sizes(output);
        for (var page = 0; page < 4; page++)
        {
            var old = oldSizes[page];
            var size = newSizes[page];
            size.ShouldBe(old.IsLandscape ? new PdfSize(A5.Height, A5.Width) : A5, $"page {page + 1}");
            var scale = Math.Min(size.Width / old.Width, size.Height / old.Height);
            var offsetX = (size.Width - (old.Width * scale)) / 2;
            var offsetY = (size.Height - (old.Height * scale)) / 2;
            foreach (var (x, y) in new[] { (28.0, 28.0), (old.Width - 28, 28.0), (28.0, old.Height - 28), (old.Width - 28, old.Height - 28) })
            {
                var expected = await ColorAtAsync(before, page, x, y);
                expected.ShouldNotBe("background", "the sample has a marker in every corner");
                var actual = await ColorAtAsync(after, page, offsetX + (x * scale), offsetY + (y * scale));
                actual.ShouldBe(expected, $"page {page + 1}, corner ({x}, {y})");
            }
        }
    }

    [Fact]
    public async Task Annotations_MoveWithTheContent()
    {
        var output = await ResizeAsync(Samples.LinksAnnots, new ChangePageSizeParams("1-2", A3.Width, A3.Height));

        var scale = Math.Min(A3.Width / 595, A3.Height / 842);
        var offsetX = (A3.Width - (595 * scale)) / 2;
        var offsetY = (A3.Height - (842 * scale)) / 2;
        var oldRects = Rects(Samples.LinksAnnots);
        var newRects = Rects(output);
        newRects.Length.ShouldBe(oldRects.Length);
        for (var i = 0; i < oldRects.Length; i++)
        {
            var (page, subtype, rect) = oldRects[i];
            newRects[i].Page.ShouldBe(page);
            newRects[i].Subtype.ShouldBe(subtype);
            var moved = page <= 1 ? rect.Select((v, k) => (v * scale) + (k % 2 == 0 ? offsetX : offsetY)).ToArray() : rect;
            newRects[i].Rect.Zip(moved).ShouldAllBe(p => Math.Abs(p.First - p.Second) < 0.01);
        }

        var oldQuads = Values(Samples.LinksAnnots, 1, "/Highlight", "/QuadPoints");
        var newQuads = Values(output, 1, "/Highlight", "/QuadPoints");
        newQuads.Select((v, k) => v - ((oldQuads[k] * scale) + (k % 2 == 0 ? offsetX : offsetY))).ShouldAllBe(d => Math.Abs(d) < 0.01);
        PdfInspector.Links(output).ShouldBe(PdfInspector.Links(Samples.LinksAnnots));
    }

    [Fact]
    public async Task DestinationsOnResizedPages_PointAtTheSameSpot()
    {
        var output = await ResizeAsync(Samples.LinksAnnots, new ChangePageSizeParams("6", A3.Width, A3.Height));

        var scale = Math.Min(A3.Width / 595, A3.Height / 842);
        var offsetY = (A3.Height - (842 * scale)) / 2;
        NamedTop(output).ShouldBe((842 * scale) + offsetY, 0.01);
        NamedTop(Samples.LinksAnnots).ShouldBe(842);

        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await PdfInspector.BookmarksAsync(after)).ShouldBe(await BookmarksOfAsync(Samples.LinksAnnots));
    }

    [Fact]
    public async Task Execute_OnFiles_WritesTheOutputAndLeavesTheInputAlone()
    {
        var input = Path.Combine(_temp.Combine("in"), "a.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        File.Copy(Samples.BasicVi, input);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(input, TestToken)));
        var progress = new RecordingProgress();
        var context = new OperationContext { InputPaths = [input], OutputDirectory = _temp.Combine("out"), WorkingDirectory = _temp.Combine("work") };

        var result = await _operation.ExecuteAsync(context, new ChangePageSizeParams("1-6", 612, 792), progress, TestToken);

        result.Succeeded.ShouldBeTrue();
        result.OutputPaths.ShouldBe([_temp.Combine("out", "a.pdf")]);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, result.OutputPaths[0]);
        (await Sizes(result.OutputPaths[0])).ShouldAllBe(s => s == new PdfSize(612, 792));
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(input, TestToken))).ShouldBe(hash);
        progress.Reports.ShouldContain(p => p.StageKey == ChangePageSizeOperation.ResizeStage && p.Completed == 6);
    }

    [Fact]
    public async Task Cancelled_WritesNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var output = new MemoryStream();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _operation.ApplyToFileAsync(Samples.BasicVi, output, new ChangePageSizeParams("1", 612, 792), new RecordingProgress(), cancelled.Token));

        output.Length.ShouldBe(0);
    }

    [Fact]
    public async Task UnreadableInput_IsInvalidData()
    {
        var garbage = _temp.Combine("garbage.pdf");
        await File.WriteAllTextAsync(garbage, "not a pdf", TestToken);
        using var output = new MemoryStream();

        await Should.ThrowAsync<InvalidDataException>(
            () => _operation.ApplyToFileAsync(garbage, output, new ChangePageSizeParams("1", 612, 792), new RecordingProgress(), TestToken));
    }

    private async Task<string> ResizeAsync(string input, ChangePageSizeParams parameters)
    {
        var output = _temp.Combine($"{Guid.NewGuid():N}.pdf");
        await using (var stream = File.Create(output))
        {
            await _operation.ApplyToFileAsync(input, stream, parameters, new RecordingProgress(), TestToken);
        }

        return output;
    }

    private async Task<PdfSize[]> Sizes(string path)
    {
        await using var loaded = await pdfium.Loader.LoadAsync(path, password: null, TestToken);
        return [.. loaded.PageSizes.Select(s => new PdfSize(Math.Round(s.Width, 1), Math.Round(s.Height, 1)))];
    }

    private async Task<string[]> BookmarksOfAsync(string path)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        return await PdfInspector.BookmarksAsync(document);
    }

    /// <summary>Where the page has ink (dark pixels), in points from the top-left corner of the page as displayed.</summary>
    private static async Task<(double Left, double Top, double Right, double Bottom)> InkBoundsAsync(PdfDocumentHandle document, int page)
    {
        const double scale = 2;
        using var bitmap = await document.RenderPageAsync(page, new PageRenderOptions { Scale = scale }, cancellationToken: TestToken);
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var (b, g, r) = bitmap.GetPixel(x, y);
                if (r < 100 && g < 100 && b < 100)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        right.ShouldBeGreaterThan(-1, "the page has ink");
        return (left / scale, top / scale, (right + 1) / scale, (bottom + 1) / scale);
    }

    /// <summary>The marker colour at a point of the page as displayed (points from the top-left corner).</summary>
    private static async Task<string> ColorAtAsync(PdfDocumentHandle document, int page, double x, double y)
    {
        using var bitmap = await document.RenderPageAsync(page, new PageRenderOptions { Scale = 1 }, cancellationToken: TestToken);
        var (b, g, r) = bitmap.GetPixel(Math.Clamp((int)x, 0, bitmap.Width - 1), Math.Clamp((int)y, 0, bitmap.Height - 1));
        return (r, g, b) switch
        {
            ( > 200, < 80, < 80) => "red",
            ( < 80, > 100, < 80) => "green",
            ( < 80, < 80, > 200) => "blue",
            ( < 60, < 60, < 60) => "black",
            _ => "background",
        };
    }

    private static (int Page, string Subtype, double[] Rect)[] Rects(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var result = new List<(int, string, double[])>();
        for (var i = 0; i < document.PageCount; i++)
        {
            foreach (var annotation in Annotations(document.Pages[i])?.Elements.Select(AsDictionary).OfType<PdfDictionary>() ?? [])
            {
                result.Add((i, Name(annotation, "/Subtype") ?? "?", PageResizer.Numbers(AsArray(annotation.Elements["/Rect"])!)!));
            }
        }

        return [.. result];
    }

    private static double[] Values(string path, int page, string subtype, string key)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var annotation = Annotations(document.Pages[page])!.Elements.Select(AsDictionary).OfType<PdfDictionary>().Single(a => Name(a, "/Subtype") == subtype);
        return PageResizer.Numbers(AsArray(annotation.Elements[key])!)!;
    }

    private static double NamedTop(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var destination = new NamedDestinations(document).Find("phu-luc")!;
        return PageResizer.Numbers(new PdfArray(document, destination.Elements[3]))![0];
    }
}

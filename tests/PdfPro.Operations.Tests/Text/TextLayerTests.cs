using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Operations.Stamping;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Text;

/// <summary>The character layer PDFium reads from pages: text, boxes in PDF user space, pointing and copying.</summary>
public sealed class TextLayerTests(PdfiumFixture pdfium) : IDisposable
{
    private const string Motto = "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM";

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ThePageTextAndItsCharacters_LineUp()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);

        for (var page = 0; page < document.PageCount; page++)
        {
            var layer = await document.GetPageTextLayerAsync(page, cancellationToken: Token);
            layer.HasText.ShouldBeTrue($"page {page + 1}");
            TextNormalizer.ToNfc(layer.Text).ShouldBe(TextNormalizer.ToNfc(await document.GetPageTextAsync(page, Token)), $"page {page + 1}");
            var size = document.PageSizes[page];
            layer.Chars.Where(c => c.IsDrawn).ShouldAllBe(c => c.Box.Left >= -1 && c.Box.Right <= size.Width + 1 && c.Box.Bottom >= -1 && c.Box.Top <= size.Height + 1);
            layer.Chars.Where(c => c.IsGenerated).ShouldAllBe(c => c.Box.IsEmpty);
        }
    }

    [Fact]
    public async Task TheMotto_CanBePointedAt_SelectedByWordAndLine_AndCopied()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        var (layer, start) = await FindAsync(document, Motto);

        var first = layer.Chars[start];
        var center = new PdfPoint((first.Box.Left + first.Box.Right) / 2, (first.Box.Bottom + first.Box.Top) / 2);
        layer.CharAt(center).ShouldBe(start);
        TextNormalizer.ToNfc(layer.GetText(layer.WordAt(start))).ShouldBe("CỘNG");

        var line = layer.LineAt(start + 10);
        layer.CopyText(line).ShouldBe(Motto);
        var rects = layer.Rects(new TextRange(start, start + Motto.Length));
        rects.Count.ShouldBe(1, "the motto is one line");
        rects[0].Width.ShouldBeGreaterThan(100);
        rects[0].Height.ShouldBeInRange(8, 30);
    }

    [Fact]
    public async Task OnTurnedPages_CharactersAreInPdfSpace_AndShowWhereTheTextIs()
    {
        // Page numbers sit at the bottom centre of each page as it is shown, whatever its /Rotate.
        var numbered = _temp.Combine("numbered.pdf");
        var output = new FileStream(numbered, FileMode.CreateNew);
        await using (output.ConfigureAwait(false))
        {
            await new PageNumbersOperation().ApplyToFileAsync(Samples.RotatedCropBox, output, new PageNumbersParams("Trang {page}"), new Progress<Core.Operations.OperationProgress>(), Token);
        }

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, numbered);
        for (var page = 0; page < document.PageCount; page++)
        {
            var geometry = await document.GetPageGeometryAsync(page, Token);
            var layer = await document.GetPageTextLayerAsync(page, cancellationToken: Token);
            var mapper = new PageCoordinateMapper(geometry, 1);
            var shown = layer.Rects(new TextRange(0, layer.Count)).Select(mapper.PdfRectToScreen).ToArray();
            var size = document.PageSizes[page];

            TextNormalizer.ToNfc(layer.Text).ShouldContain($"Trang {page + 1}");
            shown.ShouldNotBeEmpty();
            shown.ShouldAllBe(r => r.Y > size.Height - 50 && Math.Abs(r.X + (r.Width / 2) - (size.Width / 2)) < 40, $"page {page + 1}: bottom centre as shown");
        }
    }

    [Theory]
    [InlineData(@"Số:\s*\d+", true)]
    [InlineData("so:", false)]
    [InlineData("SỐ:", false)]
    public async Task SearchingTheDecisionNumbers_FindsTheFirstPageOfEachDocument(string query, bool regex)
    {
        // vanban-vi.pdf: five documents starting on pages 1, 4, 6, 8 and 11 with "Số: …".
        TextSearch.TryCreate(query, new TextSearchOptions(UseRegex: regex), out var search, out var error).ShouldBeTrue(error);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.VanBanVi);

        var pages = new List<int>();
        for (var page = 0; page < document.PageCount; page++)
        {
            var layer = await document.GetPageTextLayerAsync(page, cancellationToken: Token);
            var matches = search.FindAll(layer);
            if (matches.Count > 0)
            {
                pages.Add(page + 1);
                TextNormalizer.ToNfc(layer.GetText(matches[0])).ShouldStartWith("Số:");
            }
        }

        pages.ShouldBe([1, 4, 6, 8, 11]);
    }

    [Fact]
    public async Task AScannedPage_HasNoText()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.ScanVi);

        (await document.GetPageTextLayerAsync(0, cancellationToken: Token)).HasText.ShouldBeFalse();
    }

    /// <summary>The page layer holding <paramref name="text"/> and the index of its first character.</summary>
    private static async Task<(PageText Layer, int Start)> FindAsync(PdfDocumentHandle document, string text)
    {
        for (var page = 0; page < document.PageCount; page++)
        {
            var layer = await document.GetPageTextLayerAsync(page, cancellationToken: Token);
            layer.Chars.ShouldAllBe(c => c.Text.Length == 1, "basic-vi.pdf writes every letter as one character");
            var start = TextNormalizer.ToNfc(layer.Text).IndexOf(text, StringComparison.Ordinal);
            if (start >= 0)
            {
                return (layer, start);
            }
        }

        throw new InvalidOperationException($"'{text}' is not in the document.");
    }
}

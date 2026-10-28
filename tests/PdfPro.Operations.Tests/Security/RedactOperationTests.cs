using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Core.Redaction;
using PdfPro.Core.Text;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Security;

/// <summary>
/// True redaction (SPEC P8 B) checked from outside: the removed data cannot be read back by PDFium nor by PdfPig, from
/// the page or from anywhere else in the file (metadata, bookmarks, notes, links, fields), while the rest stays.
/// </summary>
public sealed class RedactOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RedactOperation _operation = new(pdfium.Gate);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAContentChange()
    {
        _operation.Id.ShouldBe("security.redact");
        _operation.Kind.ShouldBe(OperationKind.Content);
    }

    [Fact]
    public async Task ARedactedCccd_IsGoneForEveryReader_AndTheRestOfTheFileStays()
    {
        var hits = await FindAsync(Samples.Pii, RedactionPattern.NationalId);
        hits.Select(h => h.Text).ShouldBe(["001203004567", "079095001234", "036187009876"]);

        var output = await RedactAsync(Samples.Pii, new RedactParams(Areas(hits)));

        AllText(output).ShouldNotContain("001203004567");
        AllText(output).ShouldNotContain("079095001234");
        (await PdfiumTextAsync(output)).ShouldNotContain("036187009876");
        File.ReadAllBytes(output).AsSpan().IndexOf("001203004567"u8).ShouldBe(-1, "not even in a stream left uncompressed");
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        pdf.PageCount.ShouldBe(2);
        pdf.Info.Keywords.ShouldNotContain("001203004567", Case.Sensitive, "keywords naming the number are removed");
        pdf.Info.Author.ShouldBe(PiiName, "what does not name the removed data stays");
        pdf.Outlines.Count.ShouldBe(1, "the bookmark does not name a CCCD");
    }

    [Fact]
    public async Task RedactedPages_KeepNoTextAtAll_UnlikeTheOthers()
    {
        var hits = await FindAsync(Samples.Pii, RedactionPattern.Email);
        var onFirstPage = hits.Where(h => h.PageIndex == 0).ToList();

        var output = await RedactAsync(Samples.Pii, new RedactParams(Areas(onFirstPage)));

        using var pig = PigDocument.Open(output);
        pig.GetPage(1).Letters.ShouldBeEmpty("a redacted page is a picture: nothing on it can be selected or copied");
        Squeeze(pig.GetPage(2).Text).ShouldContain("tran.van.binh@example.org", Case.Sensitive, "an untouched page keeps its text");
        RedactOperation.ReportOf(Last!).ShouldNotBeNull().Pages.ShouldBe([1]);
    }

    [Fact]
    public async Task WhatNamesTheRemovedData_GoesToo_NotesLinksBookmarksMetadata()
    {
        // The name, the mobile number and the e-mail on page 1.
        var hits = (await FindAsync(Samples.Pii, RedactionPattern.Phone, RedactionPattern.Email)).Where(h => h.PageIndex == 0).ToList();
        var name = await RectsOfAsync(Samples.Pii, 0, PiiName);
        var areas = Areas(hits).Append(new RedactionArea(1, name)).ToList();

        var output = await RedactAsync(Samples.Pii, new RedactParams(areas));

        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        PdfGraphAnnotations(pdf, 0).ShouldBeEmpty("the note named the phone number and the mailto link sat over the e-mail");
        pdf.Info.Author.ShouldBeEmpty("the author was the person whose name was removed");
        pdf.Outlines.Count.ShouldBe(1);
        pdf.Outlines[0].Title.ShouldBe("Danh sách người liên hệ", "the bookmark naming the person went; its child moved up");
        var report = RedactOperation.ReportOf(Last!).ShouldNotBeNull();
        report.Annotations.ShouldBe(2);
        report.Bookmarks.ShouldBe(1);
        report.Metadata.ShouldContain("Author");
        (await PdfiumTextAsync(output)).ShouldNotContain("0912");
    }

    [Fact]
    public async Task ALabel_IsWrittenInTheBoxes_AndIsTheOnlyTextLeftOnThePage()
    {
        var hits = (await FindAsync(Samples.Pii, RedactionPattern.NationalId)).Where(h => h.PageIndex == 0).ToList();

        var output = await RedactAsync(Samples.Pii, new RedactParams(Areas(hits), RgbColor.Black, "Đã xóa theo quy định"));

        using var pig = PigDocument.Open(output);
        TextNormalizer.ToNfc(Squeeze(pig.GetPage(1).Text)).ShouldBe("Đãxóatheoquyđịnh");
    }

    [Fact]
    public async Task TheBox_IsPaintedWhereTheDataWas_AndTheRestLooksTheSame()
    {
        var hit = (await FindAsync(Samples.Pii, RedactionPattern.NationalId)).First();
        var output = await RedactAsync(Samples.Pii, new RedactParams(Areas([hit]), new RgbColor(200, 0, 0)));

        var box = hit.Rects[0];
        var inside = new PdfPoint((box.Left + box.Right) / 2, (box.Bottom + box.Top) / 2);
        var outside = new PdfPoint(box.Left, box.Top + 40);
        (await PixelAsync(output, 0, inside)).ShouldBe((B: (byte)0, G: (byte)0, R: (byte)200));
        Near(await PixelAsync(output, 0, outside), await PixelAsync(Samples.Pii, 0, outside)).ShouldBeTrue("the picture lines up with the page as it was");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnTurnedAndCroppedPages_ThePictureStillLinesUp(int pageIndex)
    {
        // samples/rotated-cropbox.pdf: /Rotate 90/180/270, boxes not starting at (0, 0), coloured corner marks.
        var geometry = await GeometryAsync(Samples.RotatedCropBox, pageIndex);
        var box = geometry.Box;
        var area = new PdfRect(box.Left + (box.Width * 0.4), box.Bottom + (box.Height * 0.4), box.Left + (box.Width * 0.6), box.Bottom + (box.Height * 0.6));

        var output = await RedactAsync(Samples.RotatedCropBox, new RedactParams([new RedactionArea(pageIndex + 1, [area])]));

        (await GeometryAsync(output, pageIndex)).ShouldBe(geometry, "the page keeps its boxes and turn");
        (await PixelAsync(output, pageIndex, new PdfPoint(box.Left + (box.Width / 2), box.Bottom + (box.Height / 2)))).ShouldBe((B: (byte)0, G: (byte)0, R: (byte)0));
        foreach (var corner in new[] { new PdfPoint(box.Left + 6, box.Bottom + 6), new PdfPoint(box.Right - 6, box.Top - 6), new PdfPoint(box.Left + 6, box.Top - 6), new PdfPoint(box.Right - 6, box.Bottom + 6) })
        {
            Near(await PixelAsync(output, pageIndex, corner), await PixelAsync(Samples.RotatedCropBox, pageIndex, corner)).ShouldBeTrue("each corner mark stays at its corner");
        }
    }

    [Fact]
    public async Task FormFields_OverAnAreaOrHoldingTheData_AreRemoved()
    {
        var input = _temp.Combine("form.pdf");
        using (var form = PdfReader.Open(Samples.Pii, PdfDocumentOpenMode.Modify))
        {
            FormWriter.AddFields(form,
            [
                new FormFieldSpec("ghiChu", FormFieldKind.Text, 1, new PdfRect(72, 100, 300, 120), Value: "Liên hệ 001203004567"),
                new FormFieldSpec("dongY", FormFieldKind.CheckBox, 0, new PdfRect(400, 100, 414, 114)),
                new FormFieldSpec("khac", FormFieldKind.Text, 1, new PdfRect(72, 140, 300, 160), Value: "Không liên quan"),
            ]);
            form.Save(input);
        }

        var hits = (await FindAsync(input, RedactionPattern.NationalId)).Where(h => h.PageIndex == 0).ToList();
        var areas = Areas(hits).Append(new RedactionArea(1, [new PdfRect(390, 90, 420, 120)])).ToList();

        var output = await RedactAsync(input, new RedactParams(areas));

        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        FormReader.Read(pdf).Select(f => f.Name).ShouldBe(["khac"]);
        RedactOperation.ReportOf(Last!)!.Fields.ShouldBe(2);
    }

    [Theory]
    [InlineData(0, 100, 100, 200, 200, "page")]
    [InlineData(3, 100, 100, 200, 200, "page")]
    [InlineData(1, 100, 100, 100, 200, "size")]
    public void InvalidAreas_AreRefused(int page, double left, double bottom, double right, double top, string expected)
    {
        var parameters = new RedactParams([new RedactionArea(page, [new PdfRect(left, bottom, right, top)])]);

        var result = _operation.Validate(parameters, new Core.Documents.DocumentInfo(2));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain(expected);
    }

    [Fact]
    public void ALongLabel_OrNoArea_IsRefused()
    {
        var area = new RedactionArea(1, [new PdfRect(1, 1, 2, 2)]);

        _operation.Validate(new RedactParams([area], Label: new string('x', 101)), new Core.Documents.DocumentInfo(1)).IsValid.ShouldBeFalse();
        _operation.Validate(new RedactParams([]), new Core.Documents.DocumentInfo(1)).IsValid.ShouldBeFalse();
        _operation.Validate(new RedactParams([area], Label: "Đã xóa"), new Core.Documents.DocumentInfo(1)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task TheRemovedText_IsNeverInTheParameters()
    {
        var hits = await FindAsync(Samples.Pii, RedactionPattern.NationalId);
        var json = System.Text.Json.JsonSerializer.Serialize(new RedactParams(Areas(hits)));

        json.ShouldNotContain("001203004567");
    }

    private const string PiiName = "Nguyễn Thị Minh Anh";

    private RedactParams? Last { get; set; }

    private static IReadOnlyList<RedactionArea> Areas(IEnumerable<RedactionHit> hits)
        => [.. hits.GroupBy(h => h.PageIndex).Select(g => new RedactionArea(g.Key + 1, [.. g.SelectMany(h => h.Rects)]))];

    private async Task<IReadOnlyList<RedactionHit>> FindAsync(string path, params RedactionPattern[] patterns)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        var hits = new List<RedactionHit>();
        for (var page = 0; page < document.PageCount; page++)
        {
            hits.AddRange(RedactionFinder.Find(await document.GetPageTextLayerAsync(page, cancellationToken: Token), page, patterns));
        }

        return hits;
    }

    private async Task<IReadOnlyList<PdfRect>> RectsOfAsync(string path, int page, string text)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        var layer = await document.GetPageTextLayerAsync(page, cancellationToken: Token);
        TextSearch.TryCreate(text, new TextSearchOptions(), out var search, out _).ShouldBeTrue();
        return [.. search!.FindAll(layer).SelectMany(layer.Rects)];
    }

    private async Task<string> RedactAsync(string input, RedactParams parameters)
    {
        Last = parameters;
        var output = _temp.Combine($"redacted-{Guid.NewGuid():N}.pdf");
        await using (var stream = File.Create(output))
        {
            await _operation.ApplyToFileAsync(input, stream, parameters, new RecordingProgress(), Token);
        }

        return output;
    }

    private static string AllText(string path)
    {
        using var pig = PigDocument.Open(path);
        return TextNormalizer.ToNfc(string.Concat(pig.GetPages().Select(p => Squeeze(p.Text))));
    }

    private async Task<string> PdfiumTextAsync(string path)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        return string.Concat((await PdfInspector.AllTextsAsync(document)).Select(Squeeze));
    }

    private async Task<PageGeometry> GeometryAsync(string path, int page)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        return await document.GetPageGeometryAsync(page, Token);
    }

    /// <summary>The colour PDFium shows at a point of the page (PDF space), rendered at 1 pixel per point.</summary>
    private async Task<(byte B, byte G, byte R)> PixelAsync(string path, int page, PdfPoint point)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        var geometry = await document.GetPageGeometryAsync(page, Token);
        using var bitmap = await document.RenderPageAsync(page, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        var at = new PageCoordinateMapper(geometry, 1).PdfToScreen(point);
        return bitmap.GetPixel(Math.Clamp((int)at.X, 0, bitmap.Width - 1), Math.Clamp((int)at.Y, 0, bitmap.Height - 1));
    }

    private static bool Near((byte B, byte G, byte R) a, (byte B, byte G, byte R) b)
        => Math.Abs(a.B - b.B) <= 40 && Math.Abs(a.G - b.G) <= 40 && Math.Abs(a.R - b.R) <= 40;

    private static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static IReadOnlyList<PdfSharp.Pdf.PdfDictionary> PdfGraphAnnotations(PdfSharp.Pdf.PdfDocument document, int page)
        => PdfPro.Operations.Materializing.PdfGraph.Annotations(document.Pages[page]) is { } annotations
            ? [.. annotations.Elements.Select(PdfPro.Operations.Materializing.PdfGraph.AsDictionary).OfType<PdfSharp.Pdf.PdfDictionary>()]
            : [];
}

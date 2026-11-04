using System.Globalization;
using System.Text;
using PdfPro.Core.Operations;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Security;

/// <summary>
/// "Làm sạch" (SPEC P8 C) checked on the file it writes: what a document carries beyond what it shows is gone — for
/// PDFsharp, PdfPig and PDFium alike — and the pages look as they did.
/// </summary>
public sealed class SanitizeOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private const string Secret = "Bi mat";

    private readonly TempDirectory _temp = new();
    private readonly SanitizeOperation _operation = new(pdfium.Gate);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAContentChange()
    {
        _operation.Id.ShouldBe("security.sanitize");
        _operation.Kind.ShouldBe(OperationKind.Content);
    }

    [Fact]
    public async Task EverythingBeyondWhatIsShown_IsRemoved_AndReported()
    {
        var input = WriteDirty();
        var parameters = new SanitizeParams();

        var output = await SanitizeAsync(input, parameters);

        var report = SanitizeOperation.ReportOf(parameters).ShouldNotBeNull();
        report.Metadata.ShouldContain("Author");
        report.Metadata.ShouldContain("CreationDate");
        report.MetadataStreams.ShouldBe(1);
        report.Scripts.ShouldBe(3, "the document script, the opening action and the page's action");
        report.Attachments.ShouldBe(2, "the embedded file and the attachment comment");
        report.RiskyActions.ShouldBe(2, "the submit button and the launch link");
        report.ExternalLinks.ShouldBe(0, "web links are kept unless asked");
        report.HiddenLayers.ShouldBe(1);
        report.HiddenContent.ShouldBe(1);
        report.OutsideObjects.ShouldBe(1);
        report.Thumbnails.ShouldBe(1);
        report.PrivateData.ShouldBe(2);
        report.OldRevisions.ShouldBe(1);
        report.TrailingBytes.ShouldBeGreaterThan(0);
        report.PagesLeftAsTheyWere.ShouldBeEmpty();

        using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            pdf.Info.Elements.Count.ShouldBe(0, "no author, dates or programs, not even PDFsharp's own name");
            var catalog = pdf.Internals.Catalog;
            foreach (var key in new[] { "/Metadata", "/OpenAction", "/Names", "/PieceInfo" })
            {
                catalog.Elements.ContainsKey(key).ShouldBeFalse(key);
            }

            var page = pdf.Pages[0];
            foreach (var key in new[] { "/AA", "/Thumb", "/PieceInfo" })
            {
                page.Elements.ContainsKey(key).ShouldBeFalse(key);
            }

            var annotations = Annotations(page)!.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList();
            annotations.Select(a => Name(a, "/Subtype")).ShouldBe(["/Link", "/Widget"], "the web link and the button stay; the attachment and the launch link go");
            AsDictionary(annotations[0].Elements["/A"])!.Elements.GetString("/URI").ShouldBe("https://example.vn");
            annotations[1].Elements.ContainsKey("/A").ShouldBeFalse("the button no longer sends the form");
            var layers = AsArray(AsDictionary(catalog.Elements["/OCProperties"])!.Elements["/OCGs"])!;
            layers.Elements.Select(AsDictionary).Select(l => l!.Elements.GetString("/Name")).ShouldBe(["Hien"]);
        }

        var file = await File.ReadAllBytesAsync(output, Token);
        Count(file, "%%EOF").ShouldBe(1, "written anew: one revision");
        Latin1(file).ShouldNotContain("Ban cu", Case.Sensitive, "nothing of the earlier revision is left");
        Latin1(file).ShouldNotContain("RAC SAU EOF", Case.Sensitive);
        using var pig = PigDocument.Open(output);
        var text = pig.GetPage(1).Text;
        text.ShouldContain("Noi dung");
        text.ShouldContain("Lop hien");
        text.ShouldNotContain(Secret, Case.Sensitive, "the hidden layer is gone");
        text.ShouldNotContain("Ngoai trang", Case.Sensitive, "what lay outside the visible area is gone");
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        var metadata = await document.GetMetadataAsync(Token);
        new[] { metadata.Title, metadata.Author, metadata.Creator, metadata.Producer }.ShouldAllBe(v => string.IsNullOrEmpty(v));
        metadata.CreationDate.ShouldBeNull();
    }

    [Fact]
    public async Task ExternalLinks_GoWhenAsked_AndOptionsLeftOffKeepTheirPart()
    {
        var input = WriteDirty();
        var parameters = new SanitizeParams(Metadata: false, Attachments: false, ExternalLinks: true, HiddenLayers: false, OutsideVisibleArea: false);

        var output = await SanitizeAsync(input, parameters);

        var report = SanitizeOperation.ReportOf(parameters).ShouldNotBeNull();
        report.ExternalLinks.ShouldBe(1);
        report.Metadata.ShouldBeEmpty();
        report.HiddenLayers.ShouldBe(0);
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        pdf.Info.Author.ShouldBe("Nguyễn Văn A");
        Annotations(pdf.Pages[0])!.Elements.Select(AsDictionary).Select(a => Name(a!, "/Subtype")).ShouldBe(["/FileAttachment", "/Widget"]);
        using var pig = PigDocument.Open(output);
        pig.GetPage(1).Text.ShouldContain(Secret, Case.Sensitive, "hidden layers were not asked for");
    }

    [Fact]
    public async Task AHiddenLayerThatChangesWhatFollows_IsLeft_AndThePageNamed()
    {
        // The hidden section sets the colour the visible square is filled with: dropping it would turn the square black.
        var input = _temp.Combine("colour.pdf");
        using (var pdf = new PdfDocument())
        {
            var page = pdf.AddPage();
            var (visible, hidden) = Layers(pdf, page);
            Content(pdf, page, "/OC /oc1 BDC 1 0 0 rg EMC 100 100 200 200 re f\n");
            _ = visible;
            _ = hidden;
            pdf.Save(input);
        }

        var parameters = new SanitizeParams();
        var output = await SanitizeAsync(input, parameters);

        var report = SanitizeOperation.ReportOf(parameters).ShouldNotBeNull();
        report.PagesLeftAsTheyWere.ShouldBe([1]);
        report.HiddenLayers.ShouldBe(0, "the layer still draws on page 1");
        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, input);
        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await PixelAsync(after, new PdfSharp.Drawing.XPoint(200, 200))).ShouldBe(await PixelAsync(before, new PdfSharp.Drawing.XPoint(200, 200)));
    }

    [Theory]
    [InlineData("%PDF-1.4\n...%%EOF\n", 0, 0)]
    [InlineData("%PDF-1.4\n...%%EOF\n...%%EOF\r\n", 1, 0)]
    [InlineData("%PDF-1.4\n...%%EOF\nJUNK", 0, 4)]
    [InlineData("%PDF-1.4\n<</Linearized 1>>...%%EOF\n...%%EOF\n", 0, 0)]
    public void Revisions_AndBytesAfterTheEnd_AreCounted(string file, int revisions, long trailing)
        => SanitizeOperation.History(Encoding.ASCII.GetBytes(file)).ShouldBe((revisions, trailing));

    private async Task<string> SanitizeAsync(string input, SanitizeParams parameters)
    {
        var output = _temp.Combine($"clean-{Guid.NewGuid():N}.pdf");
        await using (var stream = File.Create(output))
        {
            await _operation.ApplyToFileAsync(input, stream, parameters, new RecordingProgress(), Token);
        }

        return output;
    }

    /// <summary>
    /// One page (crop box inside the media box) with: visible text, a visible and a hidden layer, text outside the crop
    /// box; document information and XMP; a document script, an opening script and a page script; an embedded file and
    /// an attachment comment; a web link, a launch link, a submit button; a thumbnail and private data; then an
    /// incremental update and bytes after the end.
    /// </summary>
    private string WriteDirty()
    {
        var path = _temp.Combine("dirty.pdf");
        using (var pdf = new PdfDocument())
        {
            var page = pdf.AddPage();
            page.Elements["/CropBox"] = new PdfArray(pdf, new PdfInteger(0), new PdfInteger(0), new PdfInteger(595), new PdfInteger(800));
            Layers(pdf, page);
            Content(pdf, page,
                "BT /F1 12 Tf 72 700 Td (Noi dung) Tj ET\n"
                + "/OC /oc0 BDC BT /F1 12 Tf 72 650 Td (Lop hien) Tj ET EMC\n"
                + $"/OC /oc1 BDC BT /F1 12 Tf 72 600 Td ({Secret}) Tj ET EMC\n"
                + "BT /F1 12 Tf 72 820 Td (Ngoai trang) Tj ET\n");

            pdf.Info.Subject = "Ban cu";
            var catalog = pdf.Internals.Catalog;
            catalog.Elements["/Metadata"] = Stream(pdf, "<x:xmpmeta><dc:creator>Nguyễn Văn A</dc:creator></x:xmpmeta>").Reference!;
            catalog.Elements["/OpenAction"] = Action(pdf, "/JavaScript", "/JS", "app.alert('mo')");
            var names = new PdfDictionary(pdf);
            names.Elements["/JavaScript"] = NameTree(pdf, "dau", Action(pdf, "/JavaScript", "/JS", "var x = 1;"));
            var file = new PdfDictionary(pdf);
            file.Elements.SetName("/Type", "/Filespec");
            file.Elements.SetString("/F", "bang-luong.xlsx");
            var embedded = Stream(pdf, "du lieu dinh kem");
            var ef = new PdfDictionary(pdf);
            ef.Elements["/F"] = embedded.Reference!;
            file.Elements["/EF"] = ef;
            names.Elements["/EmbeddedFiles"] = NameTree(pdf, "bang-luong.xlsx", file);
            catalog.Elements["/Names"] = names;
            catalog.Elements["/PieceInfo"] = Private(pdf);

            var pageActions = new PdfDictionary(pdf);
            pageActions.Elements["/O"] = Action(pdf, "/JavaScript", "/JS", "this.print();");
            page.Elements["/AA"] = pageActions;
            page.Elements["/Thumb"] = Stream(pdf, "thumbnail").Reference!;
            page.Elements["/PieceInfo"] = Private(pdf);

            var annotations = new PdfArray(pdf);
            annotations.Elements.Add(Annotation(pdf, "/FileAttachment", new PdfDictionary(pdf)));
            annotations.Elements.Add(Annotation(pdf, "/Link", Action(pdf, "/URI", "/URI", "https://example.vn")));
            annotations.Elements.Add(Annotation(pdf, "/Link", Action(pdf, "/Launch", "/F", "cmd.exe")));
            annotations.Elements.Add(Annotation(pdf, "/Widget", Action(pdf, "/SubmitForm", "/F", "https://example.vn/gui")));
            page.Elements["/Annots"] = annotations;
            pdf.Save(path);
        }

        AppendRevision(path);
        File.AppendAllText(path, "RAC SAU EOF");
        return path;
    }

    /// <summary>Two layers on the page: /oc0 "Hien" shown, /oc1 "An" hidden by default.</summary>
    private static (PdfDictionary Visible, PdfDictionary Hidden) Layers(PdfDocument pdf, PdfPage page)
    {
        PdfDictionary Group(string name)
        {
            var group = new PdfDictionary(pdf);
            group.Elements.SetName("/Type", "/OCG");
            group.Elements.SetString("/Name", name);
            pdf.Internals.AddObject(group);
            return group;
        }

        var visible = Group("Hien");
        var hidden = Group("An");
        var configuration = new PdfDictionary(pdf);
        configuration.Elements["/OFF"] = new PdfArray(pdf, hidden.Reference!);
        configuration.Elements["/Order"] = new PdfArray(pdf, visible.Reference!, hidden.Reference!);
        var properties = new PdfDictionary(pdf);
        properties.Elements["/OCGs"] = new PdfArray(pdf, visible.Reference!, hidden.Reference!);
        properties.Elements["/D"] = configuration;
        pdf.Internals.Catalog.Elements["/OCProperties"] = properties;

        var font = new PdfDictionary(pdf);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        var fonts = new PdfDictionary(pdf);
        fonts.Elements["/F1"] = font;
        var layerNames = new PdfDictionary(pdf);
        layerNames.Elements["/oc0"] = visible.Reference!;
        layerNames.Elements["/oc1"] = hidden.Reference!;
        var resources = new PdfDictionary(pdf);
        resources.Elements["/Font"] = fonts;
        resources.Elements["/Properties"] = layerNames;
        page.Elements["/Resources"] = resources;
        return (visible, hidden);
    }

    private static void Content(PdfDocument pdf, PdfPage page, string content)
        => page.Elements["/Contents"] = Stream(pdf, content).Reference!;

    private static PdfDictionary Stream(PdfDocument pdf, string text)
    {
        var stream = new PdfDictionary(pdf);
        stream.CreateStream(Encoding.UTF8.GetBytes(text));
        pdf.Internals.AddObject(stream);
        return stream;
    }

    private static PdfDictionary Action(PdfDocument pdf, string kind, string key, string value)
    {
        var action = new PdfDictionary(pdf);
        action.Elements.SetName("/S", kind);
        action.Elements.SetString(key, value);
        return action;
    }

    private static PdfDictionary NameTree(PdfDocument pdf, string name, PdfItem value)
    {
        var tree = new PdfDictionary(pdf);
        tree.Elements["/Names"] = new PdfArray(pdf, new PdfString(name), value);
        return tree;
    }

    private static PdfDictionary Private(PdfDocument pdf)
    {
        var data = new PdfDictionary(pdf);
        data.Elements.SetString("/Private", "du lieu rieng cua phan mem");
        var info = new PdfDictionary(pdf);
        info.Elements["/Illustrator"] = data;
        return info;
    }

    private static PdfDictionary Annotation(PdfDocument pdf, string subtype, PdfDictionary action)
    {
        var annotation = new PdfDictionary(pdf);
        annotation.Elements.SetName("/Type", "/Annot");
        annotation.Elements.SetName("/Subtype", subtype);
        annotation.Elements["/Rect"] = new PdfArray(pdf, new PdfInteger(72), new PdfInteger(500), new PdfInteger(200), new PdfInteger(520));
        if (action.Elements.Count > 0)
        {
            annotation.Elements["/A"] = action;
        }

        pdf.Internals.AddObject(annotation);
        return annotation;
    }

    /// <summary>
    /// An incremental update (as a viewer saving in place writes it): the information dictionary written again, with an
    /// author, a title and a date, and without the earlier subject ("Ban cu"), which stays in the earlier revision.
    /// </summary>
    private static void AppendRevision(string path)
    {
        var file = File.ReadAllBytes(path);
        var text = Latin1(file);
        var startXref = long.Parse(System.Text.RegularExpressions.Regex.Matches(text, @"startxref\s+(\d+)")[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        var trailer = text[text.LastIndexOf("trailer", StringComparison.Ordinal)..];
        var size = int.Parse(System.Text.RegularExpressions.Regex.Match(trailer, @"/Size\s+(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var root = System.Text.RegularExpressions.Regex.Match(trailer, @"/Root\s+(\d+\s+\d+\s+R)").Groups[1].Value;
        var info = int.Parse(System.Text.RegularExpressions.Regex.Match(trailer, @"/Info\s+(\d+)\s+\d+\s+R").Groups[1].Value, CultureInfo.InvariantCulture);
        var author = Convert.ToHexString(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("Nguyễn Văn A")).ToArray());
        var update = new StringBuilder("\n");
        var objectAt = file.Length + update.Length;
        update.Append(CultureInfo.InvariantCulture, $"{info} 0 obj\n<< /Author <{author}> /Title (Ho so) /CreationDate (D:20240501000000) >>\nendobj\n");
        var xrefAt = file.Length + update.Length;
        update.Append(CultureInfo.InvariantCulture, $"xref\n{info} 1\n{objectAt:D10} 00000 n \ntrailer\n<< /Size {size} /Root {root} /Info {info} 0 R /Prev {startXref} >>\nstartxref\n{xrefAt}\n%%EOF\n");
        File.AppendAllText(path, update.ToString(), Encoding.Latin1);
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static int Count(byte[] file, string marker) => System.Text.RegularExpressions.Regex.Matches(Latin1(file), System.Text.RegularExpressions.Regex.Escape(marker)).Count;

    private static async Task<(byte B, byte G, byte R)> PixelAsync(PdfPro.Pdfium.PdfDocumentHandle document, PdfSharp.Drawing.XPoint point)
    {
        var geometry = await document.GetPageGeometryAsync(0, Token);
        using var bitmap = await document.RenderPageAsync(0, new PdfPro.Pdfium.PageRenderOptions { Scale = 1, Flags = PdfPro.Pdfium.PdfRenderFlags.None }, cancellationToken: Token);
        var at = new PdfPro.Core.Geometry.PageCoordinateMapper(geometry, 1).PdfToScreen(new PdfPro.Core.Geometry.PdfPoint(point.X, point.Y));
        return bitmap.GetPixel((int)at.X, (int)at.Y);
    }
}

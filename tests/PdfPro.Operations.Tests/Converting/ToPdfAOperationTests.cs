using System.Text;
using System.Xml.Linq;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Operations;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Converting.PdfA;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.Optimizing;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// "PDF/A" (PROMPTS P9 C5): a copy made PDF/A-2b or -1b — metadata that agrees, an sRGB output intent, what PDF/A
/// forbids removed or settled — with what is still left reported.
/// </summary>
public sealed class ToPdfAOperationTests(PdfiumFixture pdfium) : IDisposable
{
    /// <summary>Bundled fonts only: the same on every machine (no Arial for Helvetica).</summary>
    private static readonly FontLibrary Bundled = new([]);

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace XmpNs = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace PdfAId = "http://www.aiim.org/pdfa/ns/id/";

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ADocument_BecomesPdfA2b_WithMetadataAndAnOutputIntent_ItsTextKept()
    {
        var output = _temp.Combine("cơ bản (PDF-A).pdf");

        var report = await Operation().ConvertAsync(Samples.BasicVi, null, output, new ToPdfAParams(), Token);

        report.Output.ShouldBe(output);
        report.Level.ShouldBe(PdfALevel.A2B);
        report.PageCount.ShouldBe(6);
        report.Changes.Select(c => c.Fix).ShouldContain(PdfAFix.Metadata);
        report.Changes.Select(c => c.Fix).ShouldContain(PdfAFix.OutputIntent);
        report.Problems.ShouldBeEmpty("basic-vi embeds its fonts and draws in RGB");

        var bytes = await File.ReadAllBytesAsync(output, Token);
        Encoding.ASCII.GetString(bytes, 0, 9).ShouldBe("%PDF-1.7\n");
        bytes[9].ShouldBe((byte)'%');
        bytes.Skip(10).Take(4).ShouldAllBe(b => b >= 128, "a binary comment right after the header");
        Encoding.Latin1.GetString(bytes).ShouldContain("/ID[");
        Encoding.Latin1.GetString(bytes).ShouldNotContain("/Encrypt");
        Encoding.Latin1.GetString(bytes, bytes.Length - 7, 7).TrimEnd('\n', '\r').ShouldEndWith("%%EOF");

        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var xmp = Xmp(pdf, out var metadata);
        metadata.Elements.ContainsKey("/Filter").ShouldBeFalse("PDF/A wants the XMP packet unfiltered");
        xmp.Descendants(PdfAId + "part").Single().Value.ShouldBe("2");
        xmp.Descendants(PdfAId + "conformance").Single().Value.ShouldBe("B");
        Agree(pdf, xmp);

        var intent = AsDictionary(AsArray(pdf.Internals.Catalog.Elements["/OutputIntents"])!.Elements.Single())!;
        Name(intent, "/S").ShouldBe("/GTS_PDFA1");
        intent.Elements.GetString("/OutputConditionIdentifier").ShouldBe("sRGB IEC61966-2.1");
        var profile = AsDictionary(intent.Elements["/DestOutputProfile"])!;
        profile.Elements.GetInteger("/N").ShouldBe(3);
        IccProfiles.IsProfile(profile.Stream.UnfilteredValue, "RGB ", 2).ShouldBeTrue();

        Text(output).ShouldBe(Text(Samples.BasicVi));
    }

    [Fact]
    public async Task PdfA1b_IsWrittenAsPdf14_AndSaysPartOne()
    {
        var output = _temp.Combine("a1b.pdf");

        var report = await Operation().ConvertAsync(Samples.BasicVi, null, output, new ToPdfAParams(PdfALevel.A1B), Token);

        report.Level.ShouldBe(PdfALevel.A1B);
        Encoding.ASCII.GetString(await File.ReadAllBytesAsync(output, Token), 0, 9).ShouldBe("%PDF-1.4\n");
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Xmp(pdf, out _).Descendants(PdfAId + "part").Single().Value.ShouldBe("1");
    }

    [Fact]
    public async Task WhatPdfA2Forbids_IsRemovedOrSettled_AndCounted()
    {
        var (input, lzwData) = DirtyDocument();
        var output = _temp.Combine("sạch.pdf");

        var report = await Operation().ConvertAsync(input, null, output, new ToPdfAParams(), Token);

        var changes = report.Changes.ToDictionary(c => c.Fix, c => c.Count);
        changes[PdfAFix.Scripts].ShouldBe(3, "the document script, the open action and the page's additional action");
        changes[PdfAFix.Actions].ShouldBe(2, "launching a program and a Print named action");
        changes[PdfAFix.Attachments].ShouldBe(2, "an embedded file and an attachment comment");
        changes[PdfAFix.Multimedia].ShouldBe(1, "a sound comment");
        changes[PdfAFix.HiddenAnnotations].ShouldBe(1);
        changes[PdfAFix.AnnotationFlags].ShouldBe(2, "the note and the polygon get the print flag");
        changes[PdfAFix.AnnotationAppearances].ShouldBe(2, "the note had no appearance, the polygon had a rollover one");
        changes[PdfAFix.LzwStreams].ShouldBe(1);
        changes[PdfAFix.ImageInterpolation].ShouldBe(1);
        changes[PdfAFix.AlternateImages].ShouldBe(1);
        changes[PdfAFix.TransferFunctions].ShouldBe(1);
        changes[PdfAFix.ObjectMetadata].ShouldBe(1);
        changes[PdfAFix.Layers].ShouldBe(3, "the configuration is named, loses /AS and orders the second layer");
        changes[PdfAFix.SignaturesInvalidated].ShouldBe(1);
        changes.ShouldNotContainKey(PdfAFix.FlattenedAnnotations);

        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var catalog = pdf.Internals.Catalog;
        catalog.Elements.ContainsKey("/OpenAction").ShouldBeFalse();
        catalog.Elements.ContainsKey("/Names").ShouldBeFalse();
        var page = pdf.Pages[0];
        page.Elements.ContainsKey("/AA").ShouldBeFalse();
        page.Elements.ContainsKey("/Metadata").ShouldBeFalse();
        var annotations = Annotations(page)!.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList();
        annotations.Select(a => Name(a, "/Subtype")).ShouldBe(["/Text", "/Link", "/Link", "/Polygon"]);
        var note = annotations[0];
        note.Elements.GetInteger("/F").ShouldBe(4);
        AsDictionary(AsDictionary(note.Elements["/AP"])!.Elements["/N"])!.Stream.ShouldNotBeNull();
        annotations.Skip(1).Take(2).ShouldAllBe(link => !link.Elements.ContainsKey("/A"), "the launch and the named action are gone");
        AsDictionary(annotations[3].Elements["/AP"])!.Elements.Keys.ShouldBe(["/N"], "rollover appearances go");

        var resources = AsDictionary(page.Elements["/Resources"])!;
        var image = AsDictionary(resources.Elements["/XObject"])!.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>().Single(x => Name(x, "/Subtype") == "/Image");
        image.Elements.ContainsKey("/Interpolate").ShouldBeFalse();
        image.Elements.ContainsKey("/Alternates").ShouldBeFalse();
        var lzw = AsDictionary(AsDictionary(resources.Elements["/XObject"])!.Elements["/Lzw"])!;
        Name(lzw, "/Filter").ShouldBe("/FlateDecode");
        StreamData.Decode(lzw).ShouldBe(lzwData);
        var state = AsDictionary(AsDictionary(resources.Elements["/ExtGState"])!.Elements["/G0"])!;
        state.Elements.ContainsKey("/TR").ShouldBeFalse();

        var configuration = AsDictionary(AsDictionary(catalog.Elements["/OCProperties"])!.Elements["/D"])!;
        configuration.Elements.GetString("/Name").ShouldBe("Default");
        configuration.Elements.ContainsKey("/AS").ShouldBeFalse();
        AsArray(configuration.Elements["/Order"])!.Elements.Count.ShouldBe(2);

        var xmp = Xmp(pdf, out _);
        Agree(pdf, xmp);
        pdf.Info.Title.ShouldBe("Hồ sơ lưu trữ");
        pdf.Info.Elements.ContainsKey("/Producer").ShouldBeFalse("PDFsharp would rewrite it: the packet names the producer");
        pdf.Info.Elements.ContainsKey("/Trapped").ShouldBeFalse();
        xmp.Descendants(Dc + "title").Single().Value.ShouldBe("Hồ sơ lưu trữ");
    }

    [Fact]
    public async Task PdfA1_DrawsNewerCommentsIntoThePage_MakesThemOpaque_AndDropsLayers()
    {
        var (input, _) = DirtyDocument(transparent: false);
        var output = _temp.Combine("a1.pdf");
        var contents = Contents(input);

        var report = await Operation().ConvertAsync(input, null, output, new ToPdfAParams(PdfALevel.A1B), Token);

        var changes = report.Changes.ToDictionary(c => c.Fix, c => c.Count);
        changes[PdfAFix.FlattenedAnnotations].ShouldBe(1, "the polygon is not a PDF 1.4 comment");
        changes[PdfAFix.AnnotationOpacity].ShouldBe(1);
        changes[PdfAFix.Layers].ShouldBe(2);
        changes.ShouldNotContainKey(PdfAFix.PagesAsPictures);
        report.Problems.ShouldBeEmpty();
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        pdf.Internals.Catalog.Elements.ContainsKey("/OCProperties").ShouldBeFalse();
        var annotations = Annotations(pdf.Pages[0])!.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList();
        annotations.Select(a => Name(a, "/Subtype")).ShouldBe(["/Text", "/Link", "/Link"]);
        annotations[0].Elements.GetReal("/CA").ShouldBe(1);
        Contents(output).ShouldBeGreaterThan(contents, "the polygon's appearance is drawn in a stream of its own");
        pdf.Pages[0].Elements.ContainsKey("/Group").ShouldBeFalse("PDF/A-1 has no transparency groups (PDFsharp gives every page one)");
    }

    [Fact]
    public async Task PdfA1_MakesPagesThatUseTransparency_PicturesOfThemselves_TheOthersStay()
    {
        using var builder = new ImagePdf();
        var plain = builder.Page();
        var shaded = builder.Page();
        var states = new PdfDictionary(builder.Document);
        states.Elements["/Half"] = State(builder.Document, 0.5);
        builder.Resources(plain).Elements["/ExtGState"] = states;
        builder.Resources(shaded).Elements["/ExtGState"] = states;
        builder.Raw(plain, "0 0 1 rg 100 100 200 200 re f");
        builder.Raw(shaded, "0 0 1 rg 100 100 200 200 re f /Half gs 1 0 0 rg 200 200 200 200 re f");
        var input = builder.Save(_temp.Combine("trong suốt.pdf"));
        var output = _temp.Combine("1b ảnh.pdf");

        var two = await Operation().ConvertAsync(input, null, _temp.Combine("2b giữ.pdf"), new ToPdfAParams(), Token);
        var one = await Operation().ConvertAsync(input, null, output, new ToPdfAParams(PdfALevel.A1B), Token);

        two.PicturePages.ShouldBeEmpty("PDF/A-2 allows transparency");
        one.PicturePages.ShouldBe([2], "the first page has the state in its resources but never sets it");
        one.Changes.Single(c => c.Fix == PdfAFix.PagesAsPictures).Count.ShouldBe(1);
        one.Problems.ShouldBeEmpty();
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        XObjects(pdf.Pages[1]).ShouldBe(["/PdfAPage"]);
        XObjects(pdf.Pages[0]).ShouldBeEmpty();

        // The blend survives as a picture: where the red half covers the blue, purple.
        await pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfPro.Pdfium.PdfiumDocument.Open(pdfium.Gate, output);
                using var bitmap = document.RenderPage(1, new PdfPro.Pdfium.PageRenderOptions { Scale = 1 });
                var at = ((792 - 250) * bitmap.Stride) + (250 * 4);
                var (blue, green, red) = (bitmap.Pixels[at], bitmap.Pixels[at + 1], bitmap.Pixels[at + 2]);
                ((int)red).ShouldBeInRange(110, 145);
                ((int)blue).ShouldBeInRange(110, 145);
                ((int)green).ShouldBeLessThan(20);
                return 0;
            },
            PdfPro.Pdfium.PdfiumPriority.Normal,
            Token);
    }

    [Fact]
    public async Task AStandardFontThatIsNotEmbedded_IsReplacedByABundledOne_WithItsWidths()
    {
        var input = WithFont("/Type1", "/Helvetica", null, "BT /F1 24 Tf 72 700 Td (Hello Quang) Tj ET");
        var output = _temp.Combine("helvetica.pdf");

        var report = await Operation().ConvertAsync(input, null, output, new ToPdfAParams(), Token);

        report.Changes.Single(c => c.Fix == PdfAFix.FontsSubstituted).Count.ShouldBe(1, "no Arial among the bundled fonts: Noto Sans stands for Helvetica");
        report.Problems.ShouldBeEmpty();
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var font = Font(pdf);
        Name(font, "/Subtype").ShouldBe("/TrueType");
        var encoding = AsDictionary(font.Elements["/Encoding"]).ShouldNotBeNull("standard encoding differs from WinAnsi: differences are kept");
        Name(encoding, "/BaseEncoding").ShouldBe("/WinAnsiEncoding");
        AsArray(encoding.Elements["/Differences"])!.Elements.Select(e => (Resolve(e) as PdfName)?.Value).ShouldContain("/quoteright", "code 39 is a right quote in the standard encoding");
        WidthsAgree(font);
        Text(output).ShouldContain("Hello Quang");
    }

    [Fact]
    public async Task AFontOfThisComputer_IsEmbeddedAsItIs()
    {
        var input = WithFont("/TrueType", "/NotoSans-Bold", "/WinAnsiEncoding", "BT /F1 24 Tf 72 700 Td (Xin chao) Tj ET");
        var output = _temp.Combine("noto.pdf");

        var report = await Operation().ConvertAsync(input, null, output, new ToPdfAParams(PdfALevel.A1B), Token);

        report.Changes.Single(c => c.Fix == PdfAFix.FontsEmbedded).Count.ShouldBe(1);
        report.Changes.Select(c => c.Fix).ShouldNotContain(PdfAFix.FontsSubstituted);
        report.Problems.ShouldBeEmpty();
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var font = Font(pdf);
        Name(font, "/Encoding").ShouldBe("/WinAnsiEncoding", "PDF/A-1: no differences");
        var descriptor = AsDictionary(font.Elements["/FontDescriptor"])!;
        ((StreamData.Integer(descriptor, "/Flags") ?? 0) & 32).ShouldBe(32, "non-symbolic");
        var program = TrueTypeFont.Load(StreamData.Bytes(AsDictionary(descriptor.Elements["/FontFile2"])!));
        program.IsBold.ShouldBeTrue("the bold face of the family");
        WidthsAgree(font);
    }

    [Theory]
    [InlineData("/Symbol")]
    [InlineData("/.VnTime")]
    [InlineData("/VNI-Times")]
    public async Task SymbolAndOldVietnameseFonts_AreNeverReplaced_ButReported(string name)
    {
        var input = WithFont("/TrueType", name, null, "BT /F1 12 Tf 72 700 Td (abc) Tj ET");

        var report = await Operation().ConvertAsync(input, null, _temp.Combine("cũ.pdf"), new ToPdfAParams(), Token);

        report.Problems.Select(Describe).ShouldBe([$"FontNotEmbedded 1 {name.TrimStart('/')}"]);
        report.HasProblems.ShouldBeTrue();
    }

    [Fact]
    public async Task CmykColours_GetAProfile_AndKeepTheirLook()
    {
        using var builder = new ImagePdf();
        var page = builder.Page();
        var image = builder.Samples([.. Enumerable.Repeat((byte)0, 4 * 4 * 3).Concat(Enumerable.Repeat((byte)255, 4 * 4))], 4, 4, 4);
        builder.Draw(page, image, new PdfMatrix(100, 0, 0, 100, 300, 300));
        builder.Raw(page, "0 0 0 1 k 50 50 100 100 re f 1 0 0 0 K 4 w 200 50 m 280 50 l S");
        var input = builder.Save(_temp.Combine("cmyk.pdf"));
        var output = _temp.Combine("cmyk-a.pdf");

        var report = await Operation().ConvertAsync(input, null, output, new ToPdfAParams(), Token);

        report.Changes.Select(c => c.Fix).ShouldContain(PdfAFix.CmykProfile);
        report.Problems.ShouldBeEmpty();
        using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            var resources = AsDictionary(pdf.Pages[0].Elements["/Resources"])!;
            var space = AsArray(AsDictionary(resources.Elements["/ColorSpace"])!.Elements["/DefaultCMYK"])!;
            Resolve(space.Elements[0]).ShouldBeOfType<PdfName>().Value.ShouldBe("/ICCBased");
            var profile = AsDictionary(space.Elements[1])!;
            profile.Elements.GetInteger("/N").ShouldBe(4);
            IccProfiles.IsProfile(profile.Stream.UnfilteredValue, "CMYK", 2).ShouldBeTrue();
            var drawn = AsDictionary(resources.Elements["/XObject"])!.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>().Single();
            Resolve(drawn.Elements["/ColorSpace"]).ShouldBeOfType<PdfArray>("the CMYK image names the profile itself");
        }

        // The page looks as it did: the black square, the cyan line and the black image, as PDFium draws them before and after.
        var before = await PixelsAsync(input, (100, 100), (240, 50), (350, 350));
        var after = await PixelsAsync(output, (100, 100), (240, 50), (350, 350));
        for (var i = 0; i < before.Count; i++)
        {
            Math.Abs(before[i].R - after[i].R).ShouldBeLessThanOrEqualTo(16, $"point {i}");
            Math.Abs(before[i].G - after[i].G).ShouldBeLessThanOrEqualTo(16, $"point {i}");
            Math.Abs(before[i].B - after[i].B).ShouldBeLessThanOrEqualTo(16, $"point {i}");
        }

        after[1].R.ShouldBeLessThan(60, "cyan");
        after[1].G.ShouldBeGreaterThan(150);
        after[0].R.ShouldBeLessThan(60, "black");
    }

    [Fact]
    public async Task PdfA1_GivesSubsetCidFontsACidSet()
    {
        FontLibrary.Register();
        var input = _temp.Combine("cid.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
            {
                graphics.DrawString("Xin chào Việt Nam", new PdfSharp.Drawing.XFont(PdfPro.Core.Text.FontFamilies.NotoSans, 14), PdfSharp.Drawing.XBrushes.Black, 72, 72);
            }

            document.Save(input);
        }

        await Operation().ConvertAsync(input, null, _temp.Combine("cid-2b.pdf"), new ToPdfAParams(), Token);
        await Operation().ConvertAsync(input, null, _temp.Combine("cid-1b.pdf"), new ToPdfAParams(PdfALevel.A1B), Token);

        CidSets(_temp.Combine("cid-2b.pdf")).ShouldBeEmpty("PDF/A-2 does not ask for it");
        var set = CidSets(_temp.Combine("cid-1b.pdf")).ShouldHaveSingleItem();
        set[0].ShouldBe((byte)0x80, "glyph 0 is always there");
        set.Sum(b => System.Numerics.BitOperations.PopCount(b)).ShouldBeGreaterThan(10, "a glyph for each letter drawn");
        Text(_temp.Combine("cid-1b.pdf")).ShouldContain("Xin chào Việt Nam");
    }

    [Fact]
    public void TheCmykProfile_DrawsCmykAsDeviceCmykIsDrawn()
    {
        IccProfiles.IsProfile(IccProfiles.Cmyk, "CMYK", 2).ShouldBeTrue();
        IccProfiles.Lab(0, 0, 0, 0).L.ShouldBe(100, 0.01);
        IccProfiles.Lab(0, 0, 0, 1).L.ShouldBe(0, 0.01);
        var cyan = IccProfiles.Lab(1, 0, 0, 0);
        cyan.L.ShouldBe(91, 2, "sRGB cyan");
        cyan.A.ShouldBe(-50, 4);
        cyan.B.ShouldBe(-15, 4);
    }

    [Fact]
    public async Task AProtectedDocument_NeedsItsOwnerPassword_AndComesOutUnprotected()
    {
        var output = _temp.Combine("mở khóa.pdf");

        await Should.ThrowAsync<DocumentPasswordException>(() => Operation().ConvertAsync(Samples.Encrypted, null, output, new ToPdfAParams(), Token));
        await Should.ThrowAsync<DocumentPasswordException>(() => Operation().ConvertAsync(Samples.Encrypted, "test123", output, new ToPdfAParams(), Token));
        File.Exists(output).ShouldBeFalse();

        var report = await Operation().ConvertAsync(Samples.Encrypted, "chu-so-huu-456", output, new ToPdfAParams(), Token);

        report.Changes.Select(c => c.Fix).ShouldContain(PdfAFix.Decrypted);
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        pdf.PageCount.ShouldBe(report.PageCount);
        Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output, Token)).ShouldNotContain("/Encrypt");
    }

    [Fact]
    public async Task TheOperation_WritesEachInput_IntoTheOutputFolder_AndCancellingLeavesNothing()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("PDF-A")).FullName;
        var context = new OperationContext { InputPaths = [Samples.BasicVi], OutputDirectory = folder, WorkingDirectory = _temp.Path };

        var result = await Operation().ExecuteAsync(context, new ToPdfAParams(), new Progress<OperationProgress>(), Token);

        result.OutputPaths.Select(Path.GetFileName).ShouldBe(["basic-vi.pdf"]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => Operation().ConvertAsync(Samples.BasicVi, null, Path.Combine(folder, "hủy.pdf"), new ToPdfAParams(), cancelled.Token));
        Directory.GetFiles(folder).Select(Path.GetFileName).ShouldBe(["basic-vi.pdf"]);
    }

    [Theory]
    [InlineData("D:20261110143005+07'00'", "2026-11-10T14:30:05+07:00")]
    [InlineData("D:20261110143005Z", "2026-11-10T14:30:05+00:00")]
    [InlineData("D:2026", "2026-01-01T00:00:00+00:00")]
    [InlineData("20261110", "2026-11-10T00:00:00+00:00")]
    [InlineData("D:20261110143005-05'30", "2026-11-10T14:30:05-05:30")]
    [InlineData("D:2026131", null)]
    [InlineData("hôm qua", null)]
    public void PdfDates_AreRead_AndWrittenTheSameWayInBothPlaces(string text, string? expected)
    {
        var date = PdfAMetadata.ParseDate(text);

        if (expected is null)
        {
            date.ShouldBeNull();
            return;
        }

        PdfAMetadata.XmpDate(date.ShouldNotBeNull()).ShouldBe(expected);
        PdfAMetadata.ParseDate(PdfAMetadata.InfoDate(date.Value)).ShouldBe(date);
    }

    /// <summary>
    /// A page with what PDF/A-2 forbids or wants settled: scripts (document, open action, page action), a launch link
    /// and a Print named action, an embedded file and an attachment comment, a sound comment, a hidden comment, a note
    /// without appearance and flags, a polygon with rollover appearance, an interpolated image with alternates, an LZW
    /// stream, a transfer function, page XMP, a layer configuration without name with /AS, a signature value.
    /// </summary>
    private (string Path, byte[] LzwData) DirtyDocument(bool transparent = true)
    {
        using var builder = new ImagePdf();
        var document = builder.Document;
        var page = builder.Page();
        document.Info.Title = "Hồ sơ lưu trữ";
        document.Info.Author = "Nguyễn Văn A";
        document.Info.Elements.SetString("/Producer", "Máy in ảo");
        document.Info.Elements.SetName("/Trapped", "/False");
        document.Info.Elements.SetString("/CreationDate", "D:20250102030405+07'00'");

        var catalog = document.Internals.Catalog;
        catalog.Elements["/OpenAction"] = Action(document, "/JavaScript", a => a.Elements.SetString("/JS", "app.alert(1)"));
        var names = new PdfDictionary(document);
        names.Elements["/JavaScript"] = NameTree(document, Action(document, "/JavaScript", a => a.Elements.SetString("/JS", "1")));
        names.Elements["/EmbeddedFiles"] = NameTree(document, new PdfDictionary(document));
        catalog.Elements["/Names"] = names;

        var pageActions = new PdfDictionary(document);
        pageActions.Elements["/O"] = Action(document, "/JavaScript", a => a.Elements.SetString("/JS", "2"));
        page.Elements["/AA"] = pageActions;
        var pageMetadata = new PdfDictionary(document);
        pageMetadata.CreateStream(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));
        document.Internals.AddObject(pageMetadata);
        page.Elements["/Metadata"] = pageMetadata.Reference!;

        var annotations = new PdfArray(document);
        annotations.Elements.Add(Annotation(document, "/Text", 0, null));
        annotations.Elements.Add(Annotation(document, "/Link", 4, a => a.Elements["/A"] = Action(document, "/Launch", l => l.Elements.SetString("/F", "calc.exe"))));
        annotations.Elements.Add(Annotation(document, "/Link", 4, a => a.Elements["/A"] = Action(document, "/Named", n => n.Elements.SetName("/N", "/Print"))));
        annotations.Elements.Add(Annotation(document, "/Sound", 4, null));
        annotations.Elements.Add(Annotation(document, "/FileAttachment", 4, null));
        annotations.Elements.Add(Annotation(document, "/Square", 4 | 2, null));
        annotations.Elements.Add(Annotation(document, "/Polygon", 0, a =>
        {
            var appearances = new PdfDictionary(document);
            appearances.Elements["/N"] = EmptyForm(document, "0 0 1 rg 0 0 20 20 re f").Reference!;
            appearances.Elements["/R"] = EmptyForm(document, string.Empty).Reference!;
            a.Elements["/AP"] = appearances;
        }));
        page.Elements["/Annots"] = annotations;
        AsDictionary(annotations.Elements[0])!.Elements["/CA"] = new PdfReal(0.5);

        var image = builder.Samples(new byte[4 * 4 * 3], 4, 4, 3, i =>
        {
            i.Elements["/Interpolate"] = new PdfBoolean(true);
            i.Elements["/Alternates"] = new PdfArray(document);
        });
        builder.Draw(page, image, new PdfMatrix(10, 0, 0, 10, 0, 0));
        var resources = builder.Resources(page);
        var lzwData = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0 0 1 rg 20 20 10 10 re f ", 40)));
        var lzw = new PdfDictionary(document);
        lzw.CreateStream(TiffLzw.Encode(lzwData));
        lzw.Elements.SetName("/Type", "/XObject");
        lzw.Elements.SetName("/Subtype", "/Form");
        lzw.Elements.SetName("/Filter", "/LZWDecode");
        lzw.Elements["/BBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(100), new PdfInteger(100));
        document.Internals.AddObject(lzw);
        StreamData.Decode(lzw).ShouldBe(lzwData, "the test's LZW is what PDF readers decode");

        var state = new PdfDictionary(document);
        state.Elements.SetName("/TR", "/Identity");
        if (transparent)
        {
            state.Elements["/CA"] = new PdfReal(0.5);
        }
        var states = new PdfDictionary(document);
        states.Elements["/G0"] = state;
        resources.Elements["/ExtGState"] = states;
        builder.Raw(page, "/G0 gs");

        var layers = new[] { Layer(document, "Lớp 1"), Layer(document, "Lớp 2") };
        var configuration = new PdfDictionary(document);
        configuration.Elements["/Order"] = new PdfArray(document, layers[0].Reference!);
        configuration.Elements["/AS"] = new PdfArray(document);
        var properties = new PdfDictionary(document);
        properties.Elements["/OCGs"] = new PdfArray(document, [.. layers.Select(l => (PdfItem)l.Reference!)]);
        properties.Elements["/D"] = configuration;
        catalog.Elements["/OCProperties"] = properties;

        var signature = new PdfDictionary(document);
        signature.Elements.SetName("/Type", "/Sig");
        signature.Elements["/ByteRange"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(1), new PdfInteger(2), new PdfInteger(3));
        signature.Elements["/Contents"] = new PdfString("00");
        document.Internals.AddObject(signature);
        catalog.Elements["/PdfProTestSignature"] = signature.Reference!;

        var path = builder.Save(_temp.Combine($"bẩn-{Guid.NewGuid():N}.pdf"));
        AddLzwForm(path, lzw);
        return (path, lzwData);
    }

    /// <summary>Puts the LZW form in the page's XObjects (after ImagePdf wrote them).</summary>
    private static void AddLzwForm(string path, PdfDictionary lzw)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        var copy = new PdfDictionary(document);
        copy.CreateStream(lzw.Stream!.Value);
        foreach (var key in new[] { "/Type", "/Subtype", "/Filter" })
        {
            copy.Elements[key] = lzw.Elements[key];
        }

        copy.Elements["/BBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(100), new PdfInteger(100));
        document.Internals.AddObject(copy);
        var resources = AsDictionary(document.Pages[0].Elements["/Resources"])!;
        AsDictionary(resources.Elements["/XObject"])!.Elements["/Lzw"] = copy.Reference!;
        document.Save(path);
    }

    private static PdfDictionary Action(PdfDocument document, string kind, Action<PdfDictionary> fill)
    {
        var action = new PdfDictionary(document);
        action.Elements.SetName("/S", kind);
        fill(action);
        return action;
    }

    private static PdfDictionary NameTree(PdfDocument document, PdfDictionary leaf)
    {
        var tree = new PdfDictionary(document);
        tree.Elements["/Names"] = new PdfArray(document, new PdfString("a"), leaf);
        return tree;
    }

    private static PdfReference Annotation(PdfDocument document, string subtype, int flags, Action<PdfDictionary>? fill)
    {
        var annotation = new PdfDictionary(document);
        annotation.Elements.SetName("/Type", "/Annot");
        annotation.Elements.SetName("/Subtype", subtype);
        annotation.Elements["/Rect"] = new PdfArray(document, new PdfInteger(100), new PdfInteger(100), new PdfInteger(120), new PdfInteger(120));
        if (flags != 0)
        {
            annotation.Elements.SetInteger("/F", flags);
        }

        fill?.Invoke(annotation);
        document.Internals.AddObject(annotation);
        return annotation.Reference!;
    }

    private static PdfDictionary EmptyForm(PdfDocument document, string content)
    {
        var form = new PdfDictionary(document);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(20), new PdfInteger(20));
        form.CreateStream(Encoding.ASCII.GetBytes(content));
        document.Internals.AddObject(form);
        return form;
    }

    private static PdfDictionary Layer(PdfDocument document, string name)
    {
        var layer = new PdfDictionary(document);
        layer.Elements.SetName("/Type", "/OCG");
        layer.Elements["/Name"] = new PdfString(name, PdfStringEncoding.Unicode);
        document.Internals.AddObject(layer);
        return layer;
    }

    /// <summary>The catalog's XMP packet, parsed.</summary>
    private static XDocument Xmp(PdfDocument pdf, out PdfDictionary metadata)
    {
        metadata = AsDictionary(pdf.Internals.Catalog.Elements["/Metadata"]).ShouldNotBeNull();
        var text = Encoding.UTF8.GetString(metadata.Stream.Value);
        text.ShouldStartWith("<?xpacket begin=");
        text.ShouldEndWith("<?xpacket end=\"w\"?>");
        return XDocument.Parse(text);
    }

    /// <summary>The document information says what the XMP says (ISO 19005-2 6.6.2.3.1).</summary>
    private static void Agree(PdfDocument pdf, XDocument xmp)
    {
        var info = pdf.Info.Elements;
        if (info.ContainsKey("/Title"))
        {
            xmp.Descendants(Dc + "title").Descendants(Rdf + "li").Single().Value.ShouldBe(info.GetString("/Title"));
        }

        if (info.ContainsKey("/Author"))
        {
            xmp.Descendants(Dc + "creator").Descendants(Rdf + "li").Single().Value.ShouldBe(info.GetString("/Author"));
        }

        if (info.ContainsKey("/Creator"))
        {
            xmp.Descendants(XmpNs + "CreatorTool").Single().Value.ShouldBe(info.GetString("/Creator"));
        }

        foreach (var (key, name) in new[] { ("/CreationDate", "CreateDate"), ("/ModDate", "ModifyDate") })
        {
            if (info.ContainsKey(key))
            {
                var written = PdfAMetadata.ParseDate(info.GetString(key)).ShouldNotBeNull();
                DateTimeOffset.Parse(xmp.Descendants(XmpNs + name).Single().Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(written);
            }
        }

        info.ContainsKey("/ModDate").ShouldBeTrue();
    }

    private ToPdfAOperation Operation() => new(pdfium.Gate, Bundled);

    /// <summary>Colours of the first page at points given in PDF space (y up), as PDFium draws it at 72 DPI.</summary>
    private Task<List<(int R, int G, int B)>> PixelsAsync(string path, params (int X, int Y)[] points)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfPro.Pdfium.PdfiumDocument.Open(pdfium.Gate, path);
                using var bitmap = document.RenderPage(0, new PdfPro.Pdfium.PageRenderOptions { Scale = 1 });
                return points.Select(p =>
                {
                    var at = ((bitmap.Height - p.Y) * bitmap.Stride) + (p.X * 4);
                    return ((int)bitmap.Pixels[at + 2], (int)bitmap.Pixels[at + 1], (int)bitmap.Pixels[at]);
                }).ToList();
            },
            PdfPro.Pdfium.PdfiumPriority.Normal,
            Token);

    private static PdfDictionary State(PdfDocument document, double alpha)
    {
        var state = new PdfDictionary(document);
        state.Elements["/ca"] = new PdfReal(alpha);
        state.Elements["/CA"] = new PdfReal(alpha);
        return state;
    }

    /// <summary>A page drawing <paramref name="content"/> with a font that is not embedded, as /F1.</summary>
    private string WithFont(string subtype, string baseFont, string? encoding, string content)
    {
        using var builder = new ImagePdf();
        var page = builder.Page();
        var font = new PdfDictionary(builder.Document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", subtype);
        font.Elements.SetName("/BaseFont", baseFont);
        if (encoding is not null)
        {
            font.Elements.SetName("/Encoding", encoding);
        }

        var fonts = new PdfDictionary(builder.Document);
        fonts.Elements["/F1"] = font;
        builder.Resources(page).Elements["/Font"] = fonts;
        builder.Raw(page, content);
        return builder.Save(_temp.Combine($"phông-{Guid.NewGuid():N}.pdf"));
    }

    private static PdfDictionary Font(PdfDocument pdf)
        => AsDictionary(AsDictionary(AsDictionary(pdf.Pages[0].Elements["/Resources"])!.Elements["/Font"])!.Elements["/F1"])!;

    /// <summary>The widths of the font dictionary are those of the program embedded (ISO 19005-2 6.2.11.5), through its encoding.</summary>
    private static void WidthsAgree(PdfDictionary font)
    {
        var descriptor = AsDictionary(font.Elements["/FontDescriptor"])!;
        var program = TrueTypeFont.Load(StreamData.Bytes(AsDictionary(descriptor.Elements["/FontFile2"])!));
        var first = StreamData.Integer(font, "/FirstChar")!.Value;
        var widths = AsArray(font.Elements["/Widths"])!.Elements.Select(e => (Resolve(e) as PdfInteger)!.Value).ToList();
        foreach (var letter in "HelloXinchaQug")
        {
            widths[letter - first].ShouldBe((int)Math.Round(program.Advance(letter) * 1000.0 / program.UnitsPerEm, MidpointRounding.AwayFromZero), $"width of {letter}");
        }
    }

    private static List<string> XObjects(PdfPage page)
        => AsDictionary(AsDictionary(page.Elements["/Resources"])?.Elements["/XObject"])?.Elements.Keys.ToList() ?? [];

    private static List<byte[]> CidSets(string path)
    {
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return [.. pdf.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => Name(d, "/Type") == "/FontDescriptor" && d.Elements.ContainsKey("/CIDSet"))
            .Select(d => StreamData.Bytes(AsDictionary(d.Elements["/CIDSet"])!))];
    }

    private static string Describe(PdfAProblem problem) => $"{problem.Kind} {string.Join(',', problem.Pages)} {problem.Detail}";

    private static string Text(string path)
    {
        using var document = PigDocument.Open(path);
        return string.Join('\n', document.GetPages().Select(p => p.Text));
    }

    private static int Contents(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return Resolve(document.Pages[0].Elements["/Contents"]) is PdfArray array ? array.Elements.Count : 1;
    }
}

using System.Text;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Annotations;
using static PdfPro.Operations.Materializing.PdfGraph;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Annotating;

/// <summary>Annotations written as the PDF standard has them, read back by other libraries and drawn by PDFium.</summary>
public sealed class AnnotationWriterTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));
    private static readonly RgbColor Yellow = new(255, 230, 0);
    private static readonly RgbColor Red = new(220, 0, 0);
    private static readonly RgbColor Blue = new(0, 0, 220);
    private static readonly RgbColor Green = new(0, 160, 0);

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EveryKind_IsWritten_WithTheEntriesViewersRead()
    {
        AnnotationItem[] items =
        [
            Markup("m1", MarkupKind.Highlight, new PdfRect(72, 700, 300, 712)),
            Markup("m2", MarkupKind.Underline, new PdfRect(72, 680, 300, 692)),
            Markup("m3", MarkupKind.StrikeOut, new PdfRect(72, 660, 300, 672)),
            Markup("m4", MarkupKind.Squiggly, new PdfRect(72, 640, 300, 652)),
            new NoteAnnotation("n1", Yellow, 1, "Lan", Created, Created, "Kiểm tra lại ngày ký", new PdfPoint(500, 800)),
            new FreeTextAnnotation("f1", RgbColor.Black, 1, "Lan", Created, Created, "Ghi chú", new PdfRect(300, 500, 450, 540), TextStyle.Default, Red, RgbColor.White, [new PdfPoint(250, 450), new PdfPoint(300, 520)]),
            new InkAnnotation("i1", Blue, 1, "Lan", Created, Created, "", 2, [[new PdfPoint(100, 100), new PdfPoint(120, 130)], [new PdfPoint(150, 100), new PdfPoint(160, 90)]]),
            new ShapeAnnotation("s1", Red, 1, "Lan", Created, Created, "", NoteShape.Square, new PdfRect(100, 300, 200, 360), 1, null),
            new ShapeAnnotation("s2", Red, 1, "Lan", Created, Created, "", NoteShape.Circle, new PdfRect(220, 300, 320, 360), 1, Yellow),
            new LineAnnotation("l1", Red, 1, "Lan", Created, Created, "", new PdfPoint(100, 400), new PdfPoint(250, 450), 2, EndEnding: LineEnding.ClosedArrow),
            new PolyAnnotation("p1", Red, 1, "Lan", Created, Created, "", true, [new PdfPoint(350, 100), new PdfPoint(400, 100), new PdfPoint(375, 140)], 1),
            new PolyAnnotation("p2", Red, 1, "Lan", Created, Created, "", false, [new PdfPoint(420, 100), new PdfPoint(470, 140)], 1),
            new StampAnnotation("t1", Red, 1, "Lan", Created, Created, "", new PdfRect(400, 700, 550, 760), "ĐÃ DUYỆT"),
        ];

        var output = Write(Blank(new PdfSize(595, 842)), 0, items);

        using (var pig = PigDocument.Open(output))
        {
            var read = pig.GetPage(1).GetAnnotations().ToArray();
            read.Select(a => a.Type).ShouldBe(
            [
                AnnotationType.Highlight, AnnotationType.Underline, AnnotationType.StrikeOut, AnnotationType.Squiggly,
                AnnotationType.Text, AnnotationType.FreeText, AnnotationType.Ink, AnnotationType.Square, AnnotationType.Circle,
                AnnotationType.Line, AnnotationType.Polygon, AnnotationType.PolyLine, AnnotationType.Stamp,
            ]);
            read.Select(a => a.Name).ShouldBe(items.Select(i => i.Id));
            read.ShouldAllBe(a => a.HasNormalAppearance);
            read[4].Content.ShouldBe("Kiểm tra lại ngày ký");
            var quad = read[0].QuadPoints.ShouldHaveSingleItem().Points;
            quad.ToArray().Select(p => (p.X, p.Y)).ShouldBe([(72d, 712d), (300d, 712d), (72d, 700d), (300d, 700d)], "upper left, upper right, lower left, lower right");
            var rect = read[7].Rectangle;
            (rect.Left, rect.Bottom, rect.Right, rect.Top).ShouldBe((100d, 300d, 200d, 360d));
        }

        Inspect(output, 0, annotations =>
        {
            var byName = annotations.ToDictionary(a => a.Elements.GetString("/NM"));
            byName["m1"].Elements.GetString("/T").ShouldBe("Lan");
            byName["m1"].Elements.GetString("/M").ShouldBe("D:20261027093000+07'00'");
            byName["m1"].Elements.GetReal("/CA").ShouldBe(0.6);
            byName["m1"].Elements.GetInteger("/F").ShouldBe(4, "printed");
            byName["n1"].Elements.GetInteger("/F").ShouldBe(28, "a note's icon keeps its size and stays upright");
            Numbers(byName["l1"], "/L").ShouldBe([100, 400, 250, 450]);
            AsArray(byName["l1"].Elements["/LE"])!.Elements.Select(e => e.ToString()).ShouldBe(["/None", "/ClosedArrow"]);
            Numbers(byName["p1"], "/Vertices").ShouldBe([350, 100, 400, 100, 375, 140]);
            AsArray(byName["i1"].Elements["/InkList"])!.Elements.Count.ShouldBe(2);
            Name(byName["f1"], "/IT").ShouldBe("/FreeTextCallout");
            Numbers(byName["f1"], "/CL").ShouldBe([250, 450, 300, 520]);
            Numbers(byName["f1"], "/C").ShouldBe([1, 1, 1], "a text box's /C is its background");
            Numbers(byName["s2"], "/IC").ShouldBe([1, 230 / 255.0, 0], tolerance: 0.001);
            annotations.ShouldAllBe(a => Resolve(a.Elements["/P"]) is PdfPage);
        });
    }

    [Fact]
    public async Task AHighlight_ColoursTheText_WhichStaysReadable()
    {
        var (rect, page) = await MottoAsync();
        var highlight = Markup("h", MarkupKind.Highlight, rect) with { Opacity = 1 };

        var output = Write(Samples.BasicVi, page, highlight);

        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        var shown = new PageCoordinateMapper(await after.GetPageGeometryAsync(page, Token), 1).PdfRectToScreen(rect);
        using var plain = await RenderAsync(before, page);
        using var marked = await RenderAsync(after, page);
        var area = Inset(shown, 1);
        var (darkBefore, darkAfter) = (Count(plain, area, IsDark), Count(marked, area, IsDark));
        darkAfter.ShouldBeGreaterThan((int)(darkBefore * 0.9), "the letters stay dark: the highlight multiplies, it does not cover");
        Count(marked, area, IsYellowish).ShouldBeGreaterThan((int)(area.Width * area.Height * 0.3), "the paper around the letters turns yellow");
        Count(plain, area, IsYellowish).ShouldBe(0);
    }

    [Fact]
    public async Task ShapesLinesAndInk_AreDrawnWhereTheyWereMade()
    {
        var output = Write(
            Blank(new PdfSize(400, 400)),
            0,
            new ShapeAnnotation("sq", Red, 1, "", Created, Created, "", NoteShape.Square, new PdfRect(50, 50, 150, 150), 2, Red),
            new ShapeAnnotation("ci", Blue, 1, "", Created, Created, "", NoteShape.Circle, new PdfRect(200, 200, 300, 260), 2, Blue),
            new InkAnnotation("in", Green, 1, "", Created, Created, "", 4, [[new PdfPoint(50, 300), new PdfPoint(150, 300)]]),
            new LineAnnotation("li", Red, 1, "", Created, Created, "", new PdfPoint(200, 100), new PdfPoint(350, 100), 2, EndEnding: LineEnding.ClosedArrow));

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var bitmap = await RenderAsync(document, 0);

        // Shown y = 400 - PDF y.
        IsColor(bitmap, 100, 300, Red).ShouldBeTrue("inside the square");
        IsColor(bitmap, 250, 170, Blue).ShouldBeTrue("centre of the ellipse");
        IsColor(bitmap, 203, 143, Blue).ShouldBeFalse("a corner of the ellipse's box is outside it");
        IsColor(bitmap, 100, 100, Green).ShouldBeTrue("on the stroke");
        IsColor(bitmap, 100, 106, Green).ShouldBeFalse("beside the stroke");
        IsColor(bitmap, 275, 300, Red).ShouldBeTrue("on the line");
        IsColor(bitmap, 343, 302, Red).ShouldBeTrue("the arrow head is wider than the line");
    }

    [Fact]
    public async Task TextBoxesAndStamps_AreUprightAsThePageIsShown_InVietnamese()
    {
        await using var source = await PdfInspector.OpenAsync(pdfium.Gate, Samples.RotatedCropBox);
        var geometries = await PdfInspector.GeometriesAsync(source);
        var output = Samples.RotatedCropBox;
        for (var i = 0; i < geometries.Length; i++)
        {
            var mapper = new PageCoordinateMapper(geometries[i], 1);
            output = Write(
                output,
                i,
                new FreeTextAnnotation($"f{i}", RgbColor.Black, 1, "Lan", Created, Created, "Đã xem", mapper.ScreenRectToPdf(new ViewRect(60, 60, 220, 40)), TextStyle.Default with { FontSize = 14 }, null, null, [], geometries[i].RotationDegrees),
                new StampAnnotation($"s{i}", Red, 1, "Lan", Created, Created, "", mapper.ScreenRectToPdf(new ViewRect(60, 140, 160, 50)), "ĐÃ DUYỆT", Rotation: geometries[i].RotationDegrees));
        }

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        for (var i = 0; i < geometries.Length; i++)
        {
            using var bitmap = await RenderAsync(document, i);
            var ink = InkBounds(bitmap, new ViewRect(60, 60, 220, 40), IsDark);
            ink.ShouldNotBeNull($"page {i + 1}: the text is drawn");
            ink.Value.Width.ShouldBeGreaterThan(ink.Value.Height * 1.5, $"page {i + 1}: one line across, as the page is shown");
            ink.Value.X.ShouldBeLessThan(75, $"page {i + 1}: starting at the left of the box");
            ink.Value.Y.ShouldBeLessThan(76, $"page {i + 1}: at the top of the box");
            ink.Value.Right.ShouldBeLessThan(170, $"page {i + 1}: a short word stays left");

            IsColor(bitmap, 61, 165, Red).ShouldBeTrue($"page {i + 1}: the stamp's frame, left");
            IsColor(bitmap, 218, 165, Red).ShouldBeTrue($"page {i + 1}: the stamp's frame, right");
            var letters = InkBounds(bitmap, new ViewRect(66, 146, 148, 38), (r, g, b) => r > 150 && g < 100 && b < 100);
            letters.ShouldNotBeNull($"page {i + 1}: the stamp's words");
            letters.Value.Width.ShouldBeGreaterThan(letters.Value.Height * 2, $"page {i + 1}: upright words");
        }
    }

    [Fact]
    public async Task AStampPicture_FillsItsBox()
    {
        var picture = _temp.Combine("dau.png");
        await File.WriteAllBytesAsync(picture, TestImages.Png(40, 20, (_, _) => (0, 0, 220, 255)), Token);
        var stamp = new StampAnnotation("p", Red, 1, "Lan", Created, Created, "", new PdfRect(100, 100, 200, 150), null, picture);

        var output = Write(Blank(new PdfSize(300, 300)), 0, stamp);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var bitmap = await RenderAsync(document, 0);
        IsColor(bitmap, 150, 175, Blue).ShouldBeTrue("centre of the picture");
        IsColor(bitmap, 103, 153, Blue).ShouldBeTrue("near its corner");
        IsColor(bitmap, 90, 175, Blue).ShouldBeFalse("outside");
    }

    [Fact]
    public void RepliesAndReviews_AreThreadedUnderTheirComment_WithoutDrawing()
    {
        var note = new NoteAnnotation("n", Yellow, 1, "Lan", Created, Created, "Số liệu quý 3?", new PdfPoint(100, 700))
        {
            Replies = [new AnnotationReply("n-r1", "Minh", Created.AddHours(1), "Đã sửa theo báo cáo mới")],
            Reviews = [new AnnotationReview("n-s1", "Minh", Created.AddHours(2), ReviewState.Accepted)],
        };

        var output = Write(Blank(new PdfSize(595, 842)), 0, note);

        Inspect(output, 0, annotations =>
        {
            annotations.Length.ShouldBe(3);
            var (comment, reply, review) = (annotations[0], annotations[1], annotations[2]);
            reply.Elements.GetString("/Contents").ShouldBe("Đã sửa theo báo cáo mới");
            reply.Elements.GetString("/T").ShouldBe("Minh");
            Resolve(reply.Elements["/IRT"]).ShouldBeSameAs(comment);
            Name(reply, "/RT").ShouldBe("/R");
            Resolve(review.Elements["/IRT"]).ShouldBeSameAs(comment);
            Name(review, "/State").ShouldBe("/Accepted");
            Name(review, "/StateModel").ShouldBe("/Review");
            reply.Elements.GetInteger("/F").ShouldBe(28);
            var appearance = AsDictionary(AsDictionary(reply.Elements["/AP"])!.Elements["/N"])!;
            appearance.Stream.Value.ShouldBeEmpty("a reply draws nothing on the page");
        });

        using var pig = PigDocument.Open(output);
        var read = pig.GetPage(1).GetAnnotations().ToArray();
        read[1].InReplyTo.ShouldNotBeNull().Name.ShouldBe("n");
    }

    [Fact]
    public void Comments_AreStoredComposed()
    {
        var note = new NoteAnnotation("n", Yellow, 1, "Nguyễn", Created, Created, "Hoà Bình", new PdfPoint(100, 700));

        var output = Write(Blank(new PdfSize(300, 800)), 0, note);

        using var pig = PigDocument.Open(output);
        pig.GetPage(1).GetAnnotations().Single().Content.ShouldBe("Hoà Bình");
    }

    [Fact]
    public void TheAnnotationsAlreadyThere_Stay()
    {
        var before = PdfInspector.Annotations(Samples.LinksAnnots);
        before[0].ShouldNotBeEmpty("the sample has links on its first page");

        var output = Write(Samples.LinksAnnots, 0, new NoteAnnotation("n", Yellow, 1, "Lan", Created, Created, "", new PdfPoint(100, 700)));

        var after = PdfInspector.Annotations(output);
        after[0].Length.ShouldBe(before[0].Length + 1);
        after[0][..^1].Select(a => a.Subtype).ShouldBe(before[0].Select(a => a.Subtype));
        after[0][^1].Subtype.ShouldBe("/Text");
        after[0][^1].OwnerPage.ShouldBe(0);
        after.Skip(1).Select(a => a.Length).ShouldBe(before.Skip(1).Select(a => a.Length));
    }

    [Fact]
    public void AnInvalidAnnotation_IsRefused_BeforeAnythingIsWritten()
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        AnnotationItem[] items =
        [
            new NoteAnnotation("ok", Yellow, 1, "Lan", Created, Created, "", new PdfPoint(100, 700)),
            new LineAnnotation("dot", Red, 1, "Lan", Created, Created, "", new PdfPoint(5, 5), new PdfPoint(5, 5), 1),
        ];

        var error = Should.Throw<ArgumentException>(() => AnnotationWriter.Write(document, page, items));

        error.Message.ShouldContain("dot");
        page.Elements["/Annots"].ShouldBeNull();
    }

    private async Task<(PdfRect Rect, int Page)> MottoAsync()
    {
        const string motto = "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM";
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        for (var page = 0; page < document.PageCount; page++)
        {
            var layer = await document.GetPageTextLayerAsync(page, cancellationToken: Token);
            var start = TextNormalizer.ToNfc(layer.Text).IndexOf(motto, StringComparison.Ordinal);
            if (start >= 0)
            {
                return (layer.Rects(new TextRange(start, start + motto.Length)).ShouldHaveSingleItem(), page);
            }
        }

        throw new InvalidOperationException("The motto is not in the sample.");
    }

    private static MarkupAnnotation Markup(string id, MarkupKind kind, PdfRect line)
        => new(id, Yellow, 0.6, "Lan", Created, Created, "Số liệu sai", kind, [TextQuad.Of(line)]);

    private string Write(string input, int pageIndex, params AnnotationItem[] items)
    {
        var output = _temp.Combine($"annotated-{Guid.NewGuid():N}.pdf");
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        AnnotationWriter.Write(document, document.Pages[pageIndex], items);
        document.Save(output);
        return output;
    }

    private string Blank(PdfSize size)
    {
        var path = _temp.Combine($"blank-{Guid.NewGuid():N}.pdf");
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(size.Width, size.Height));
        var content = new PdfContent(document);
        content.CreateStream(Encoding.ASCII.GetBytes(string.Empty));
        document.Internals.AddObject(content);
        page.Elements["/Contents"] = content.Reference!;
        document.Save(path);
        return path;
    }

    /// <summary>Runs checks against the annotation dictionaries of a page, in file order.</summary>
    private static void Inspect(string path, int pageIndex, Action<PdfDictionary[]> check)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        check([.. Annotations(document.Pages[pageIndex])!.Elements.Select(AsDictionary).OfType<PdfDictionary>()]);
    }

    private static double[] Numbers(PdfDictionary dictionary, string key)
        => [.. AsArray(dictionary.Elements[key])!.Elements.Select(e => e switch
        {
            PdfReal real => real.Value,
            PdfInteger integer => integer.Value,
            _ => double.NaN,
        })];

    private static async Task<PageBitmap> RenderAsync(PdfDocumentHandle document, int page)
        => await document.RenderPageAsync(page, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.Annotations }, cancellationToken: Token);

    private static bool IsColor(PageBitmap bitmap, double x, double y, RgbColor color)
    {
        var (b, g, r) = bitmap.GetPixel((int)Math.Round(x), (int)Math.Round(y));
        return Math.Abs(r - color.R) <= 50 && Math.Abs(g - color.G) <= 50 && Math.Abs(b - color.B) <= 50;
    }

    private static bool IsDark(byte r, byte g, byte b) => r < 90 && g < 90 && b < 90;

    private static bool IsYellowish(byte r, byte g, byte b) => r > 200 && g > 180 && b < 120;

    private static int Count(PageBitmap bitmap, ViewRect area, Func<byte, byte, byte, bool> matches)
    {
        var count = 0;
        for (var y = (int)area.Y; y < (int)area.Bottom; y++)
        {
            for (var x = (int)area.X; x < (int)area.Right; x++)
            {
                var (b, g, r) = bitmap.GetPixel(x, y);
                count += matches(r, g, b) ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>The smallest rectangle holding the matching pixels of an area, or null when none match.</summary>
    private static ViewRect? InkBounds(PageBitmap bitmap, ViewRect area, Func<byte, byte, byte, bool> matches)
    {
        var (left, top, right, bottom) = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        for (var y = (int)area.Y; y < (int)area.Bottom; y++)
        {
            for (var x = (int)area.X; x < (int)area.Right; x++)
            {
                var (b, g, r) = bitmap.GetPixel(x, y);
                if (matches(r, g, b))
                {
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x + 1), Math.Max(bottom, y + 1));
                }
            }
        }

        return left == int.MaxValue ? null : new ViewRect(left, top, right - left, bottom - top);
    }

    private static ViewRect Inset(ViewRect rect, double by) => new(rect.X + by, rect.Y + by, rect.Width - (2 * by), rect.Height - (2 * by));
}

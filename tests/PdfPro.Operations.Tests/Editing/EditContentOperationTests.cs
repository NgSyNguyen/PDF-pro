using PdfPro.Core.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Text;
using PdfPro.Operations.Editing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Editing;

/// <summary>edit.content on samples/: rewriting, removing and moving the drawings of a page.</summary>
public sealed class EditContentOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private const string Motto = "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM";

    private readonly TempDirectory _temp = new();
    private readonly EditContentOperation _operation = new(pdfium.Gate);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAContentChange()
    {
        _operation.Id.ShouldBe("edit.content");
        _operation.Kind.ShouldBe(OperationKind.Content);
    }

    [Fact]
    public async Task Words_TheFontCanWrite_KeepTheFont_AndReplaceTheOldOnes()
    {
        var motto = await MottoAsync();
        var words = "HÒA NAM CỘNG";
        EditContentOperation.MissingCharacters(Samples.BasicVi, 0, motto.Text!.FontName, words).ShouldBeEmpty("every letter is in the motto");

        var output = await ApplyAsync(new ReplaceTextEdit(PageObjectRef.Of(1, motto), words));

        var text = PageText(output, 1);
        text.ShouldContain(Squeeze(words));
        text.ShouldNotContain(Squeeze(Motto));
        var after = await ObjectsAsync(output);
        var rewritten = after.Single(o => o.Kind == PageObjectKind.Text && TextNormalizer.ToNfc(o.Text!.Text) == words);
        rewritten.Text!.FontName.ShouldBe(motto.Text.FontName, "the font had every glyph");
        rewritten.Bounds.Left.ShouldBe(motto.Bounds.Left, 1);
        rewritten.Bounds.Bottom.ShouldBe(motto.Bounds.Bottom, 2);
        after.Count.ShouldBe((await ObjectsAsync(Samples.BasicVi)).Count);
    }

    [Fact]
    public async Task Words_TheFontCannotWrite_TakeABundledFont_AtTheSamePlace()
    {
        var motto = await MottoAsync();
        var words = "Ứng dụng quản lý ỹ ẵ";
        EditContentOperation.MissingCharacters(Samples.BasicVi, 0, motto.Text!.FontName, words).ShouldNotBeEmpty("a subset has only the letters it uses");

        var output = await ApplyAsync(new ReplaceTextEdit(PageObjectRef.Of(1, motto), words));

        PageText(output, 1).ShouldContain(Squeeze(words), customMessage: "found and copied as typed");
        var rewritten = (await ObjectsAsync(output)).Single(o => o.Kind == PageObjectKind.Text && TextNormalizer.ToNfc(o.Text!.Text) == words);
        rewritten.Text!.FontName.ShouldNotBe(motto.Text.FontName);
        rewritten.Text.FontName.ShouldContain("Noto");
        rewritten.Text.SizeOnPage(rewritten.Matrix).ShouldBe(motto.Text.SizeOnPage(motto.Matrix), 0.01);
        rewritten.Bounds.Left.ShouldBe(motto.Bounds.Left, 2);
        rewritten.Bounds.Bottom.ShouldBe(motto.Bounds.Bottom, 3);
    }

    [Fact]
    public async Task LongerWords_CanBeWrappedToTheOldWidth()
    {
        var motto = await MottoAsync();
        var words = "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM";

        var output = await ApplyAsync(new ReplaceTextEdit(PageObjectRef.Of(1, motto), words, TextOverflow.Wrap));

        var lines = (await ObjectsAsync(output)).Where(o => o.Kind == PageObjectKind.Text && TextNormalizer.ToNfc(o.Text!.Text).StartsWith("CỘNG", StringComparison.Ordinal)).ToList();
        lines.Count.ShouldBeGreaterThanOrEqualTo(2);
        lines.ShouldAllBe(l => l.Bounds.Width <= motto.Bounds.Width + 2, "no wider than the old text");
        lines[1].Bounds.Top.ShouldBeLessThan(lines[0].Bounds.Bottom + 1, "the next line is below");
        PageText(output, 1).ShouldContain(Squeeze("NAM CỘNG"));
    }

    [Fact]
    public async Task Removing_AndMoving_ChangeOnlyThoseObjects()
    {
        var objects = await ObjectsAsync(Samples.BasicVi);
        var motto = await MottoAsync();
        var other = objects.First(o => o.Kind == PageObjectKind.Text && o.Index != motto.Index && o.Bounds.Top < motto.Bounds.Bottom - 50);

        var output = await ApplyAsync(new DeleteObjectEdit(PageObjectRef.Of(1, motto)), new MoveObjectEdit(PageObjectRef.Of(1, other), 30, -20));

        PageText(output, 1).ShouldNotContain(Squeeze(Motto));
        var after = await ObjectsAsync(output);
        after.Count.ShouldBe(objects.Count - 1);
        var moved = after.Single(o => o.Kind == PageObjectKind.Text && o.Text!.Text.Trim() == other.Text!.Text.Trim() && Math.Abs(o.Bounds.Left - (other.Bounds.Left + 30)) < 1);
        moved.Bounds.Bottom.ShouldBe(other.Bounds.Bottom - 20, 1);

        // Everything else is drawn as before: away from the two objects the page renders the same.
        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        await using var edited = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var a = await before.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        using var b = await edited.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        PdfRect[] changed = [Grow(motto.Bounds), Grow(other.Bounds), Grow(moved.Bounds)];
        var height = a.Height;
        var differences = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var point = new PdfPoint(x + 0.5, height - y - 0.5);
                if (changed.Any(r => r.Contains(point)))
                {
                    continue;
                }

                var (b1, g1, r1) = a.GetPixel(x, y);
                var (b2, g2, r2) = b.GetPixel(x, y);
                if (Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(b1 - b2) > 24)
                {
                    differences++;
                }
            }
        }

        differences.ShouldBeLessThan(20, "the rest of the page is untouched");
    }

    [Fact]
    public async Task AnObjectThatIsGone_IsNotReplacedByAnother()
    {
        var motto = await MottoAsync();
        var gone = PageObjectRef.Of(1, motto) with { Bounds = new PdfRect(1, 1, 2, 2) };

        await Should.ThrowAsync<InvalidDataException>(() => ApplyAsync(new DeleteObjectEdit(gone)));
    }

    [Fact]
    public void BadChanges_AreRefused()
    {
        var target = new PageObjectRef(1, 0, PageObjectKind.Text, new PdfRect(0, 0, 10, 10));
        var document = new Core.Documents.DocumentInfo(2);

        _operation.Validate(new EditContentParams([new ReplaceTextEdit(target, "ok")]), document).IsValid.ShouldBeTrue();
        _operation.Validate(new EditContentParams([]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditContentParams([new ReplaceTextEdit(target with { Page = 3 }, "ok")]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditContentParams([new ReplaceTextEdit(target with { Kind = PageObjectKind.Image }, "ok")]), document).IsValid.ShouldBeFalse("an image has no words");
        _operation.Validate(new EditContentParams([new MoveObjectEdit(target, 0, 0)]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditContentParams([new MoveObjectEdit(target, double.NaN, 1)]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditContentParams([new DeleteObjectEdit(target), new MoveObjectEdit(target, 1, 1)]), document).IsValid.ShouldBeFalse("removed before");
    }

    private static PdfRect Grow(PdfRect rect) => new(rect.Left - 4, rect.Bottom - 4, rect.Right + 4, rect.Top + 4);

    private static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>The text of a page as PdfPig reads it, NFC, without white space.</summary>
    private static string PageText(string path, int page)
    {
        using var document = PigDocument.Open(path);
        return TextNormalizer.ToNfc(string.Concat(document.GetPage(page).Letters.Select(l => l.Value).Where(v => !string.IsNullOrWhiteSpace(v))));
    }

    private async Task<PageObjectInfo> MottoAsync()
        => (await ObjectsAsync(Samples.BasicVi)).First(o => o.Kind == PageObjectKind.Text && TextNormalizer.ToNfc(o.Text!.Text).Contains(Motto, StringComparison.Ordinal));

    private async Task<IReadOnlyList<PageObjectInfo>> ObjectsAsync(string path)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        return await document.GetPageObjectsAsync(0, cancellationToken: Token);
    }

    private async Task<string> ApplyAsync(params ContentEdit[] edits)
    {
        var output = _temp.Combine($"edited-{Guid.NewGuid():N}.pdf");
        var stream = File.Create(output);
        await using (stream)
        {
            await _operation.ApplyToFileAsync(Samples.BasicVi, stream, new EditContentParams(edits), new RecordingProgress(), Token);
        }

        return output;
    }
}

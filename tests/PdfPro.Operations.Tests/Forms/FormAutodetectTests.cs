using PdfPro.Core.Editing;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Tests.Forms;

/// <summary>forms.autodetect on a printed form drawn with PDFsharp and read back by PDFium, as the viewer reads it.</summary>
public sealed class FormAutodetectTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task APrintedForm_GivesItsBlanksLinesAndBoxes_WithTheirLabels()
    {
        var path = PrintedForm();
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        var text = await document.GetPageTextLayerAsync(0, cancellationToken: Token);
        var objects = await document.GetPageObjectsAsync(0, cancellationToken: Token);

        var suggestions = FormAutodetect.Suggest(text, objects, []);

        suggestions.Select(s => (s.Kind, s.Label)).ShouldBe(
        [
            (FormFieldKind.Text, "Họ và tên"),
            (FormFieldKind.Text, "Ngày sinh"),
            (FormFieldKind.Text, "Nơi sinh"),
            (FormFieldKind.Text, "Địa chỉ"),
            (FormFieldKind.CheckBox, "Tôi đã đọc kỹ điều khoản"),
        ], "the underlined heading is not a blank");
        var name = suggestions[0].Rect;
        name.Left.ShouldBe(127, 5, "after the label");
        name.Right.ShouldBeGreaterThan(290, "to the last dot");
        name.Bottom.ShouldBe(792 - 100 - 4, 6, "on the line of the dots");
        var address = suggestions[3].Rect;
        address.Left.ShouldBe(150, 1);
        address.Right.ShouldBe(450, 1);
        address.Bottom.ShouldBe(792 - 200, 2, "standing on the drawn line");
        var box = suggestions[4].Rect;
        box.Width.ShouldBe(12, 2, "the square with its stroke");
        box.Height.ShouldBe(12, 2);
    }

    [Fact]
    public async Task PlacesTakenByFields_AreLeftOut()
    {
        var path = PrintedForm();
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        var text = await document.GetPageTextLayerAsync(0, cancellationToken: Token);
        var objects = await document.GetPageObjectsAsync(0, cancellationToken: Token);
        var all = FormAutodetect.Suggest(text, objects, []);

        var rest = FormAutodetect.Suggest(text, objects, [all[0].Rect, all[3].Rect]);

        rest.Select(s => s.Label).ShouldBe(["Ngày sinh", "Nơi sinh", "Tôi đã đọc kỹ điều khoản"]);
    }

    [Fact]
    public void BoxCharacters_BecomeCheckBoxes_LabelledByTheWordsAfterThem()
    {
        var chars = new List<TextChar>();
        var x = 100.0;
        foreach (var c in "Giới tính: ☐ Nam ☐ Nữ")
        {
            chars.Add(new TextChar(c.ToString(), new PdfRect(x, 700, x + 8, 712)));
            x += 8;
        }

        var suggestions = FormAutodetect.Suggest(new PageText(chars), [], []);

        suggestions.Select(s => (s.Kind, s.Label)).ShouldBe([(FormFieldKind.CheckBox, "Nam"), (FormFieldKind.CheckBox, "Nữ")]);
    }

    [Fact]
    public void ShortRunsOfDots_AreNotBlanks()
    {
        var chars = "Xem trang 5... và tiếp".Select((c, i) => new TextChar(c.ToString(), new PdfRect(100 + (i * 6), 700, 106 + (i * 6), 712))).ToList();

        FormAutodetect.Suggest(new PageText(chars), [new PageObjectInfo(0, PageObjectKind.Path, new PdfRect(100, 600, 400, 680), [], PdfMatrix.Identity, null)], []).ShouldBeEmpty("an ellipsis and a big frame ask for nothing");
    }

    /// <summary>
    /// A form: "Họ và tên: ……" (dots), "Ngày sinh: ____ Nơi sinh: ____" (underscores), "Địa chỉ:" before a drawn line,
    /// a drawn square before "Tôi đã đọc kỹ điều khoản", and an underlined heading.
    /// </summary>
    private string PrintedForm()
    {
        FontLibrary.Register();
        var path = _temp.Combine($"to-khai-{Guid.NewGuid():N}.pdf");
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(612);
        page.Height = XUnit.FromPoint(792);
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            var font = new XFont(FontFamilies.NotoSans, 11, XFontStyleEx.Regular);
            var bold = new XFont(FontFamilies.NotoSans, 14, XFontStyleEx.Bold);
            graphics.DrawString("TỜ KHAI", bold, XBrushes.Black, 260, 60, XStringFormats.BaseLineLeft);
            graphics.DrawLine(new XPen(XColors.Black, 1), 258, 63, 330, 63);
            graphics.DrawString("Họ và tên: " + new string('.', 60), font, XBrushes.Black, 72, 100, XStringFormats.BaseLineLeft);
            graphics.DrawString("Ngày sinh: " + new string('_', 12) + "    Nơi sinh: " + new string('_', 20), font, XBrushes.Black, 72, 140, XStringFormats.BaseLineLeft);
            graphics.DrawString("Địa chỉ:", font, XBrushes.Black, 72, 198, XStringFormats.BaseLineLeft);
            graphics.DrawLine(new XPen(XColors.Black, 0.75), 150, 200, 450, 200);
            graphics.DrawRectangle(new XPen(XColors.Black, 0.75), 72, 240, 12, 12);
            graphics.DrawString("Tôi đã đọc kỹ điều khoản", font, XBrushes.Black, 90, 250, XStringFormats.BaseLineLeft);
        }

        document.Save(path);
        return path;
    }
}

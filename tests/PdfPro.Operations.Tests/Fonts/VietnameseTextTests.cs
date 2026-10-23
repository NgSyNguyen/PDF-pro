using System.Text;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Fonts;

/// <summary>
/// SPEC P5 / "Tiếng Việt": text PDF_Pro writes with each bundled font reads back exactly (after NFC) with two
/// independent extractors — PdfPig and PDFium — and the font is embedded as a subset, so the file shows the same on a
/// computer without it.
/// </summary>
public sealed class VietnameseTextTests(PdfiumFixture pdfium) : IDisposable
{
    private const string Motto = "Cộng hòa xã hội chủ nghĩa Việt Nam — Độc lập - Tự do - Hạnh phúc";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string, bool, bool> Faces()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var family in FontFamilies.Bundled)
        {
            foreach (var (bold, italic) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                data.Add(family, bold, italic);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Faces))]
    public async Task TheMotto_ReadsBackExactly_FromEveryBundledFace(string family, bool bold, bool italic)
    {
        var path = Write(family, bold, italic, Motto, "ẳ ỡ ự Ỷ " + VietnameseText.Letters);

        using (var pig = PigDocument.Open(path))
        {
            var lines = pig.GetPage(1).GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom)).OrderByDescending(g => g.Key)
                .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)).Normalize(NormalizationForm.FormC))
                .ToArray();
            lines.ShouldBe([Motto, "ẳ ỡ ự Ỷ " + VietnameseText.Letters], $"PdfPig reading {family}");
        }

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        var text = (await PdfInspector.TextsAsync(document, 0))[0].Normalize(NormalizationForm.FormC);
        text.ShouldBe(Motto + " ẳ ỡ ự Ỷ " + VietnameseText.Letters, $"PDFium reading {family}");
    }

    [Fact]
    public void TheFont_IsEmbeddedAsASubset()
    {
        var path = Write(FontFamilies.BeVietnamPro, false, false, Motto);

        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var fonts = document.Pages[0].Resources.Elements.GetDictionary("/Font")!;
        var font = fonts.Elements.Values.Select(v => (v as PdfSharp.Pdf.Advanced.PdfReference)?.Value ?? v).OfType<PdfDictionary>().Single();
        var baseFont = font.Elements.GetName("/BaseFont");
        baseFont.ShouldMatch(@"^/[A-Z]{6}\+Be Vietnam Pro", "a subset tag, and the font's own name (never the library's face key)");
        var descendant = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)font.Elements.GetArray("/DescendantFonts")!.Elements[0]).Value;
        var descriptor = descendant.Elements.GetDictionary("/FontDescriptor")!;
        descriptor.Elements.ContainsKey("/FontFile2").ShouldBeTrue("TrueType outlines are embedded");
        font.Elements.ContainsKey("/ToUnicode").ShouldBeTrue("so the text can be searched and copied");
        new FileInfo(path).Length.ShouldBeLessThan(60_000, "only the used glyphs are embedded");
    }

    private string Write(string family, bool bold, bool italic, params string[] lines)
    {
        FontLibrary.Register();
        var style = (bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        var path = _temp.Combine($"{family}-{bold}-{italic}.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using (var graphics = XGraphics.FromPdfPage(page))
            {
                var font = new XFont(family, 12, style);
                for (var i = 0; i < lines.Length; i++)
                {
                    graphics.DrawString(lines[i], font, XBrushes.Black, 40, 80 + (i * 40));
                }
            }

            document.Save(path);
        }

        return path;
    }
}

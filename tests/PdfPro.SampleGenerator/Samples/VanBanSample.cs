using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// vanban-vi.pdf: the five fake documents of <see cref="LegalBundle"/> one after another (14 A4 pages, with text),
/// each with a top-level bookmark. First pages carry the national motto and the "Số: …" line; other pages do not.
/// </summary>
internal sealed class VanBanSample : ISample
{
    private const double PageHeight = 842;

    public string FileName => "vanban-vi.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create("Tập văn bản hành chính mẫu", "Tách theo văn bản, theo bookmark (dữ liệu giả)");
        foreach (var fake in LegalBundle.Documents)
        {
            PdfPage? first = null;
            for (var p = 0; p < fake.Pages.Length; p++)
            {
                var page = SampleDocument.AddA4Page(document);
                first ??= page;
                using var graphics = XGraphics.FromPdfPage(page);
                var writer = new PageWriter(graphics);
                if (p == 0)
                {
                    DrawHeading(graphics, writer, fake);
                }

                foreach (var paragraph in fake.Pages[p])
                {
                    writer.Paragraph(paragraph);
                }

                writer.Footer(string.Create(CultureInfo.InvariantCulture, $"Trang {p + 1}/{fake.Pages.Length}"), PageHeight);
            }

            var outline = document.Outlines.Add(fake.Bookmark, first!, opened: true);
            outline.Top = PageHeight;
        }

        document.Save(path);
    }

    /// <summary>The usual two-column heading: issuer and number left, nation and motto right, then kind and subject.</summary>
    public static void DrawHeading(XGraphics graphics, PageWriter writer, FakeLegalDocument fake)
    {
        var bold = new XFont("Times New Roman", 12, XFontStyleEx.Bold);
        var normal = PageWriter.Body;
        var italic = new XFont("Times New Roman", 12, XFontStyleEx.Italic);
        Center(graphics, fake.Issuer[0], normal, 50, 260, 72);
        Center(graphics, fake.Issuer[1], bold, 50, 260, 88);
        Center(graphics, fake.Number, normal, 50, 260, 112);
        Center(graphics, LegalBundle.Nation, bold, 250, 345, 72);
        Center(graphics, LegalBundle.Motto, bold, 250, 345, 88);
        Center(graphics, LegalBundle.Place, italic, 250, 345, 112);
        writer.Space(80);
        writer.Centered(fake.Kind, new XFont("Times New Roman", 15, XFontStyleEx.Bold));
        writer.Centered(fake.Subject, bold);
        writer.Space(12);
    }

    private static void Center(XGraphics graphics, string text, XFont font, double left, double width, double top)
        => graphics.DrawString(text, font, XBrushes.Black, new XRect(left, top, width, font.Height), XStringFormats.Center);
}

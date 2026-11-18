using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// A PDF typed with TCVN3 (".VnTime", ".VnTimeH") and VNI ("VNI-Times") fonts — written here byte by byte, the fonts not
/// embedded, as old documents often are — reads as Vietnamese in the viewer's text layer (selecting, copying,
/// searching), "Sang văn bản" and "Sang Word".
/// </summary>
public sealed class LegacyFontTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task TheTextLayer_ReadsVietnamese()
    {
        var path = Write();

        var layer = await pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                return document.GetPageTextLayer(0);
            },
            PdfiumPriority.Normal,
            Token);

        var text = layer.Text.Normalize(NormalizationForm.FormC);
        text.ShouldContain("CỘNG HOÀ XÃ HỘI CHỦ NGHĨA VIỆT NAM");
        text.ShouldContain("Độc lập - Tự do - Hạnh phúc");
        text.ShouldContain("Kính gửi Uỷ ban nhân dân xã");
        text.ShouldContain("Arial é");
        layer.Chars.Count(c => c.IsDrawn).ShouldBeGreaterThan(80, "every glyph keeps its box");
        Core.Text.TextSearch.TryCreate("nhân dân", new Core.Text.TextSearchOptions(), out var search, out _).ShouldBeTrue();
        search!.FindAll(layer).ShouldNotBeEmpty("search finds the Vietnamese");
    }

    [Fact]
    public async Task TextAndWordExports_AreVietnamese_InUnicodeFonts()
    {
        var path = Write();

        var text = await new ToTextOperation().WriteAsync(path, null, _temp.Path, "cũ", new ToTextParams(), null, Token);
        var word = await new ToWordOperation().WriteAsync(path, null, _temp.Combine("cũ.docx"), new ToWordParams(), null, Token);

        var plain = (await File.ReadAllTextAsync(text.Files.Single(), Token)).Normalize(NormalizationForm.FormC);
        plain.ShouldContain("CỘNG HOÀ XÃ HỘI CHỦ NGHĨA VIỆT NAM");
        plain.ShouldContain("Kính gửi Uỷ ban nhân dân xã");
        using var zip = ZipFile.OpenRead(word.Output);
        var xml = XDocument.Load(zip.GetEntry("word/document.xml")!.Open());
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        string.Concat(xml.Descendants(w + "t").Select(t => t.Value)).Normalize(NormalizationForm.FormC).ShouldContain("Độc lập - Tự do - Hạnh phúc");
        xml.Descendants(w + "rFonts").Select(f => (string?)f.Attribute(w + "ascii")).ShouldNotContain(f => f != null && (f.StartsWith(".Vn", StringComparison.Ordinal) || f.StartsWith("VNI", StringComparison.Ordinal)), "the Unicode look-alike fonts");
    }

    /// <summary>One page: a TCVN3 capital heading, a TCVN3 line, a VNI line, an Arial line (left alone).</summary>
    private string Write()
    {
        var path = _temp.Combine("văn bản cũ.pdf");
        string[] fonts = [".VnTimeH", ".VnTime", "VNI-Times", "Arial"];
        (int Font, string Text)[] lines =
        [
            (0, "céng hoµ x· héi chñ nghÜa viÖt nam"),
            (1, "§éc lËp - Tù do - H¹nh phóc"),
            (2, "Kính göûi Uyû ban nhaân daân xaõ"),
            (3, "Arial é"),
        ];
        var content = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            content.Append(System.Globalization.CultureInfo.InvariantCulture, $"BT /F{lines[i].Font + 1} 14 Tf 72 {720 - (i * 30)} Td (");
            foreach (var c in lines[i].Text)
            {
                content.Append(c is '(' or ')' or '\\' ? "\\" + c : c.ToString());
            }

            content.Append(") Tj ET\n");
        }

        var widths = "[" + string.Join(' ', Enumerable.Repeat("500", 224)) + "]";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R /F3 7 0 R /F4 8 0 R >> >> >>",
        };
        var data = Encoding.Latin1.GetBytes(content.ToString());
        objects.Add($"<< /Length {data.Length} >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream");
        // Fonts are objects 5 to 8, their descriptors 9 to 12.
        for (var i = 0; i < fonts.Length; i++)
        {
            objects.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /{fonts[i].Replace(" ", "#20", StringComparison.Ordinal)} /FirstChar 32 /LastChar 255 /Widths {widths} /Encoding /WinAnsiEncoding /FontDescriptor {9 + i} 0 R >>");
        }

        foreach (var font in fonts)
        {
            objects.Add($"<< /Type /FontDescriptor /FontName /{font.Replace(" ", "#20", StringComparison.Ordinal)} /Flags 32 /FontBBox [0 -215 1000 891] /ItalicAngle 0 /Ascent 891 /Descent -216 /CapHeight 662 /StemV 80 >>");
        }

        using var output = new MemoryStream();
        void Put(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        Put("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Put($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Position;
        Put($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Put($"{offset:D10} 00000 n \n");
        }

        Put($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }
}

using System.Text;
using PdfSharp.Drawing;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// broken.pdf: a valid 3-page document whose tail (cross-reference table, trailer, startxref, %%EOF) was cut off.
/// Every object is still present, so repair tools (Phase 9) can rebuild it; plain loading must fail cleanly.
/// </summary>
internal sealed class BrokenSample : ISample
{
    public string FileName => "broken.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create("Tài liệu bị hỏng", "Kiểm thử xử lý tệp hỏng");
        for (var i = 1; i <= 3; i++)
        {
            var page = SampleDocument.AddA4Page(document);
            using var graphics = XGraphics.FromPdfPage(page);
            var writer = new PageWriter(graphics);
            writer.Heading($"Trang {i} của tài liệu bị cắt cụt", PageWriter.Heading1);
            writer.Paragraph("Bảng tham chiếu chéo và phần trailer ở cuối tệp đã bị cắt bỏ để mô phỏng tệp tải về không trọn vẹn.");
        }

        using var buffer = new MemoryStream();
        document.Save(buffer);
        var bytes = buffer.ToArray();

        var xref = Encoding.Latin1.GetString(bytes).LastIndexOf("\nxref", StringComparison.Ordinal);
        if (xref < 0)
        {
            throw new InvalidOperationException("Cross-reference table not found; the generator needs updating.");
        }

        File.WriteAllBytes(path, bytes[..(xref + 1)]);
    }
}

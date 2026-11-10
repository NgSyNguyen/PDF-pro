using System.Globalization;
using PdfSharp.Drawing;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// tables.pdf: tables in Vietnamese for table extraction (SPEC P11). Page 1: a ruled table (every cell boxed; 4
/// columns, a header and 6 rows) between paragraphs. Page 2: a table without rules (columns lined up by their text
/// only; 4 columns, a header and 8 rows). All names and amounts are invented.
/// </summary>
internal sealed class TablesSample : ISample
{
    public static readonly string[] RuledHeader = ["STT", "Họ và tên", "Đơn vị", "Số tiền (đồng)"];

    public static readonly string[][] RuledRows =
    [
        ["1", "Nguyễn Văn An", "Phòng Kế toán", "1.250.000"],
        ["2", "Trần Thị Bích", "Phòng Hành chính", "980.000"],
        ["3", "Lê Hoàng Cường", "Phòng Kỹ thuật", "2.400.000"],
        ["4", "Phạm Thu Dung", "Phòng Kinh doanh", "1.575.000"],
        ["5", "Võ Minh Đức", "Ban Giám đốc", "3.100.000"],
        ["6", "Đặng Ngọc Ánh", "Phòng Kế toán", "865.000"],
    ];

    public static readonly string[] PlainHeader = ["Mã hàng", "Tên hàng", "Số lượng", "Đơn giá"];

    public static readonly string[][] PlainRows =
    [
        ["VPP-01", "Giấy in A4", "120", "65.000"],
        ["VPP-02", "Bút bi xanh", "300", "4.500"],
        ["VPP-03", "Kẹp tài liệu", "85", "12.000"],
        ["VPP-04", "Bìa hồ sơ", "200", "8.500"],
        ["VPP-05", "Sổ ghi chép", "60", "27.000"],
        ["VPP-06", "Mực máy in", "15", "450.000"],
        ["VPP-07", "Băng dính", "40", "9.000"],
        ["VPP-08", "Ghim bấm", "90", "6.000"],
    ];

    public string FileName => "tables.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create("Bảng mẫu tiếng Việt (dữ liệu giả)", "Trích bảng ra Excel và CSV");
        var first = SampleDocument.AddA4Page(document);
        using (var graphics = XGraphics.FromPdfPage(first))
        {
            var writer = new PageWriter(graphics);
            writer.Heading("BẢNG THANH TOÁN TIỀN CÔNG TÁC PHÍ (DỮ LIỆU GIẢ)", PageWriter.Heading1);
            writer.Paragraph("Bảng dưới đây có kẻ ô đầy đủ; mọi tên và số tiền đều là giả định để kiểm thử việc trích bảng.");
            Ruled(graphics, writer.Y + 8, [40, 160, 140, 111]);
        }

        var second = SampleDocument.AddA4Page(document);
        using (var graphics = XGraphics.FromPdfPage(second))
        {
            var writer = new PageWriter(graphics);
            writer.Heading("DANH SÁCH VĂN PHÒNG PHẨM (DỮ LIỆU GIẢ)", PageWriter.Heading1);
            writer.Paragraph("Bảng dưới đây không có đường kẻ: các cột chỉ được căn thẳng hàng theo chữ.");
            Plain(graphics, writer.Y + 8, [72, 170, 330, 440]);
        }

        document.Save(path);
    }

    private static void Ruled(XGraphics graphics, double top, double[] widths)
    {
        const double RowHeight = 22;
        var rows = new List<string[]> { RuledHeader };
        rows.AddRange(RuledRows);
        var pen = new XPen(XColors.Black, 0.8);
        var left = 72.0;
        var right = left + widths.Sum();
        for (var r = 0; r <= rows.Count; r++)
        {
            graphics.DrawLine(pen, left, top + (r * RowHeight), right, top + (r * RowHeight));
        }

        var x = left;
        for (var c = 0; c <= widths.Length; c++)
        {
            graphics.DrawLine(pen, x, top, x, top + (rows.Count * RowHeight));
            if (c < widths.Length)
            {
                x += widths[c];
            }
        }

        for (var r = 0; r < rows.Count; r++)
        {
            x = left;
            var font = r == 0 ? PageWriter.BodyBold : PageWriter.Body;
            for (var c = 0; c < widths.Length; c++)
            {
                graphics.DrawString(rows[r][c], font, XBrushes.Black, new XRect(x + 4, top + (r * RowHeight), widths[c] - 8, RowHeight), c == 3 && r > 0 ? XStringFormats.CenterRight : XStringFormats.CenterLeft);
                x += widths[c];
            }
        }
    }

    private static void Plain(XGraphics graphics, double top, double[] columns)
    {
        const double RowHeight = 20;
        var rows = new List<string[]> { PlainHeader };
        rows.AddRange(PlainRows);
        for (var r = 0; r < rows.Count; r++)
        {
            var font = r == 0 ? PageWriter.BodyBold : PageWriter.Body;
            for (var c = 0; c < columns.Length; c++)
            {
                graphics.DrawString(rows[r][c], font, XBrushes.Black, new XPoint(columns[c], top + ((r + 1) * RowHeight)), XStringFormats.BottomLeft);
            }
        }

        graphics.DrawString(string.Create(CultureInfo.InvariantCulture, $"Tổng số mặt hàng: {PlainRows.Length}"), PageWriter.Body, XBrushes.Black, new XPoint(72, top + ((rows.Count + 2) * RowHeight)), XStringFormats.BottomLeft);
    }
}

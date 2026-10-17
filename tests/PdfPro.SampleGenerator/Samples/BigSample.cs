using System.Globalization;
using PdfSharp.Drawing;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// big-1000.pdf: 1,000 A4 pages with text, vector shapes and images, for performance and memory tests.
/// Five images are shared by all pages so the file stays small while every page still decodes an image.
/// </summary>
internal sealed class BigSample : ISample
{
    public const int PageCount = 1000;

    private const double PageHeight = 842;

    public string FileName => "big-1000.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create("Tài liệu lớn 1.000 trang", "Kiểm thử hiệu năng và bộ nhớ");
        var images = Enumerable.Range(0, 5)
            .Select(i => XImage.FromStream(new MemoryStream(BitmapFactory.CreatePattern(480, 320, i))))
            .ToArray();
        XColor[] colors = [XColors.SteelBlue, XColors.IndianRed, XColors.SeaGreen, XColors.DarkOrange, XColors.MediumPurple];

        try
        {
            for (var i = 0; i < PageCount; i++)
            {
                var page = SampleDocument.AddA4Page(document);
                using var graphics = XGraphics.FromPdfPage(page);
                var writer = new PageWriter(graphics);
                var number = (i + 1).ToString(CultureInfo.InvariantCulture);

                writer.Heading($"Tài liệu lớn — trang {number} / {PageCount}", PageWriter.Heading1);
                graphics.DrawImage(images[i % images.Length], 72, writer.Y + 6, 451, 300);
                writer.Space(318);
                writer.Paragraph($"Đây là trang số {number} của tài liệu kiểm thử hiệu năng. Trình xem phải hiện trang đầu "
                    + "dưới 2 giây, cuộn mượt và giữ bộ nhớ ổn định khi đi từ đầu đến cuối tài liệu.");
                writer.Paragraph("Mỗi trang có chữ, hình vẽ vector và một ảnh dùng chung để mô phỏng tài liệu thực tế "
                    + "như báo cáo, hồ sơ scan hoặc giáo trình nhiều hình minh họa.");

                for (var k = 0; k < 6; k++)
                {
                    var brush = new XSolidBrush(XColor.FromArgb(160, colors[(i + k) % colors.Length]));
                    graphics.DrawEllipse(brush, 72 + (k * 75), writer.Y + 10, 60, 60);
                }

                writer.Footer($"Trang {number}/{PageCount}", PageHeight);
            }

            for (var part = 0; part < PageCount / 100; part++)
            {
                var first = (part * 100) + 1;
                var outline = document.Outlines.Add(
                    string.Create(CultureInfo.InvariantCulture, $"Phần {part + 1} (trang {first}–{first + 99})"),
                    document.Pages[part * 100],
                    opened: false);
                outline.Top = PageHeight;
            }

            document.Save(path);
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }
}

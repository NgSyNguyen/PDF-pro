using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// pii.pdf: two A4 pages of FAKE personal data for redaction by pattern (SPEC P8): CCCD numbers, Vietnamese mobile and
/// landline numbers, e-mail addresses, bank account numbers and birth dates. The data shows up elsewhere too, as a real
/// file would: the author and keywords of the document information, a bookmark title, a sticky note and a mailto link.
/// Every number and address here is invented.
/// </summary>
internal sealed class PiiSample : ISample
{
    public const string Name = "Nguyễn Thị Minh Anh";
    public const string NationalId = "001203004567";
    public const string Phone = "0912 345 678";
    public const string Email = "minhanh.nguyen@example.com";

    private const double PageHeight = 842;

    public string FileName => "pii.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create("Hồ sơ khách hàng (dữ liệu giả)", "Kiểm thử xóa thông tin cá nhân theo mẫu");
        document.Info.Author = Name;
        document.Info.Keywords = $"khách hàng; CCCD {NationalId}";

        var first = SampleDocument.AddA4Page(document);
        using (var graphics = XGraphics.FromPdfPage(first))
        {
            var writer = new PageWriter(graphics);
            writer.Heading("HỒ SƠ KHÁCH HÀNG (DỮ LIỆU GIẢ)", PageWriter.Heading1);
            foreach (var line in new[]
            {
                $"Họ và tên: {Name}",
                $"Số CCCD: {NationalId}",
                "Ngày sinh: 15/08/1990",
                $"Điện thoại di động: {Phone}",
                "Điện thoại cơ quan: 024 3825 1234",
                $"Email: {Email}",
                "Số tài khoản: 1903 6655 4433 21 (Ngân hàng Giả Định)",
                "Địa chỉ: số 12 đường Hoa Sữa, phường Láng Hạ, Hà Nội",
            })
            {
                writer.Paragraph(line);
            }

            writer.Space(12);
            writer.Paragraph("Khách hàng đồng ý cho ngân hàng liên hệ qua điện thoại và thư điện tử nêu trên. Mọi số liệu trong tệp này là giả.");
        }

        var second = SampleDocument.AddA4Page(document);
        using (var graphics = XGraphics.FromPdfPage(second))
        {
            var writer = new PageWriter(graphics);
            writer.Heading("DANH SÁCH NGƯỜI LIÊN HỆ (DỮ LIỆU GIẢ)", PageWriter.Heading1);
            foreach (var line in new[]
            {
                "1. Trần Văn Bình — CCCD 079095001234 — ĐT +84 98 765 4321 — tran.van.binh@example.org",
                "2. Lê Thị Cúc — CCCD 036187009876 — ĐT 0387 112 233 — STK 0071000123456 — sinh ngày 02/01/1985",
                "3. Phạm Quốc Dũng — ĐT 0909.555.777 — dung.pham@example.net — sinh ngày 30-04-1975",
            })
            {
                writer.Paragraph(line);
            }
        }

        // Where the data also hides: a sticky note naming the phone number and a link to the address.
        first.Annotations.Add(new PdfTextAnnotation(document)
        {
            Title = "Nhân viên tư vấn",
            Contents = $"Gọi lại số {Phone} trước thứ Sáu.",
            Icon = PdfTextAnnotationIcon.Comment,
            Rectangle = new PdfRectangle(new XPoint(500, 760), new XPoint(520, 780)),
        });
        first.AddWebLink(new PdfRectangle(new XPoint(72, 600), new XPoint(400, 640)), "mailto:" + Email);

        var bookmark = document.Outlines.Add($"Hồ sơ {Name}", first, opened: true);
        bookmark.Top = PageHeight;
        var contacts = bookmark.Outlines.Add("Danh sách người liên hệ", second, opened: true);
        contacts.Top = PageHeight;

        document.Save(path);
    }
}

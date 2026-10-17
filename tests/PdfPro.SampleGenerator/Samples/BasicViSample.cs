using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// basic-vi.pdf: 6 A4 pages of Vietnamese text with a 3-level bookmark tree, like a document exported from Word.
/// All content is fake.
/// </summary>
internal sealed class BasicViSample : ISample
{
    public const string AllLowercaseDiacritics =
        "à á ả ã ạ ă ằ ắ ẳ ẵ ặ â ầ ấ ẩ ẫ ậ đ è é ẻ ẽ ẹ ê ề ế ể ễ ệ ì í ỉ ĩ ị ò ó ỏ õ ọ ô ồ ố ổ ỗ ộ ơ ờ ớ ở ỡ ợ ù ú ủ ũ ụ ư ừ ứ ử ữ ự ỳ ý ỷ ỹ ỵ";

    public const string AllUppercaseDiacritics =
        "À Á Ả Ã Ạ Ă Ằ Ắ Ẳ Ẵ Ặ Â Ầ Ấ Ẩ Ẫ Ậ Đ È É Ẻ Ẽ Ẹ Ê Ề Ế Ể Ễ Ệ Ì Í Ỉ Ĩ Ị Ò Ó Ỏ Õ Ọ Ô Ồ Ố Ổ Ỗ Ộ Ơ Ờ Ớ Ở Ỡ Ợ Ù Ú Ủ Ũ Ụ Ư Ừ Ứ Ử Ữ Ự Ỳ Ý Ỷ Ỹ Ỵ";

    private const int PageCount = 6;
    private const double PageHeight = 842;

    public string FileName => "basic-vi.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create("Tài liệu mẫu tiếng Việt", "Kiểm thử hiển thị, tìm kiếm và dấu trang");
        var pages = Enumerable.Range(0, PageCount).Select(_ => SampleDocument.AddA4Page(document)).ToArray();

        WriteCover(pages[0]);

        var chapter1Y = 0.0;
        var article1Y = 0.0;
        var clause1Y = 0.0;
        Write(pages[1], 2, w =>
        {
            chapter1Y = w.Heading("Chương I. QUY ĐỊNH CHUNG", PageWriter.Heading1);
            article1Y = w.Heading("Điều 1. Phạm vi điều chỉnh", PageWriter.Heading2);
            w.Paragraph("Quy định này hướng dẫn việc lập, lưu trữ và trao đổi tài liệu điện tử trong nội bộ một tổ chức giả định. "
                + "Văn bản được dùng để kiểm tra khả năng hiển thị tiếng Việt có dấu, xuống dòng và căn lề của trình xem PDF.");
            w.Paragraph("Việc số hóa hồ sơ phải bảo đảm tính toàn vẹn, tính xác thực và khả năng truy xuất lâu dài. "
                + "Mỗi tệp được đặt tên thống nhất, có ghi rõ ngày tháng và người chịu trách nhiệm cập nhật.");
            clause1Y = w.Heading("Khoản 1. Đối tượng áp dụng", PageWriter.Heading3);
            w.Paragraph("Quy định áp dụng đối với cán bộ, nhân viên và cộng tác viên của tổ chức giả định khi soạn thảo, "
                + "ký duyệt hoặc chia sẻ tài liệu với các đơn vị bên ngoài.");
            Filler(w, "Nội dung bổ sung", 4);
        });

        var article2Y = 0.0;
        Write(pages[2], 3, w =>
        {
            article2Y = w.Heading("Điều 2. Giải thích từ ngữ", PageWriter.Heading2);
            w.Paragraph("Trong quy định này, các từ ngữ dưới đây được hiểu như sau:");
            w.Paragraph("1. Tài liệu điện tử là thông tin được tạo lập, gửi đi, nhận và lưu trữ bằng phương tiện điện tử.", indent: 18);
            w.Paragraph("2. Chữ ký số là một dạng chữ ký điện tử dùng cặp khóa để xác thực người ký và bảo đảm tính toàn vẹn.", indent: 18);
            w.Paragraph("3. Bản sao lưu là bản sao của tài liệu gốc được lưu ở vị trí khác nhằm phục hồi khi xảy ra sự cố.", indent: 18);
            Filler(w, "Ghi chú", 5);
        });

        var chapter2Y = 0.0;
        var article3Y = 0.0;
        Write(pages[3], 4, w =>
        {
            chapter2Y = w.Heading("Chương II. TỔ CHỨC THỰC HIỆN", PageWriter.Heading1);
            article3Y = w.Heading("Điều 3. Trách nhiệm thi hành", PageWriter.Heading2);
            w.Paragraph("Thủ trưởng các đơn vị có trách nhiệm phổ biến, hướng dẫn và kiểm tra việc thực hiện quy định này. "
                + "Bộ phận công nghệ thông tin bảo đảm hạ tầng lưu trữ, sao lưu định kỳ và phục hồi khi cần thiết.");
            Filler(w, "Hướng dẫn chi tiết", 5);
        });

        var article4Y = 0.0;
        Write(pages[4], 5, w =>
        {
            article4Y = w.Heading("Điều 4. Hiệu lực", PageWriter.Heading2);
            w.Paragraph("Quy định có hiệu lực kể từ ngày ký. Trong quá trình thực hiện, nếu phát sinh vướng mắc, các đơn vị phản ánh "
                + "về bộ phận hành chính để tổng hợp, trình xem xét sửa đổi, bổ sung cho phù hợp.");
            Filler(w, "Điều khoản chuyển tiếp", 5);
        });

        var appendixY = 0.0;
        Write(pages[5], 6, w =>
        {
            appendixY = w.Heading("PHỤ LỤC. Danh sách đầu mối (dữ liệu giả)", PageWriter.Heading1);
            DrawTable(w);
        });

        AddBookmark(document.Outlines, "Trang bìa", pages[0], 72);
        var chapter1 = AddBookmark(document.Outlines, "Chương I. Quy định chung", pages[1], chapter1Y);
        var article1 = AddBookmark(chapter1.Outlines, "Điều 1. Phạm vi điều chỉnh", pages[1], article1Y);
        AddBookmark(article1.Outlines, "Khoản 1. Đối tượng áp dụng", pages[1], clause1Y);
        AddBookmark(chapter1.Outlines, "Điều 2. Giải thích từ ngữ", pages[2], article2Y);
        var chapter2 = AddBookmark(document.Outlines, "Chương II. Tổ chức thực hiện", pages[3], chapter2Y);
        AddBookmark(chapter2.Outlines, "Điều 3. Trách nhiệm thi hành", pages[3], article3Y);
        AddBookmark(chapter2.Outlines, "Điều 4. Hiệu lực", pages[4], article4Y);
        AddBookmark(document.Outlines, "Phụ lục", pages[5], appendixY);

        document.Save(path);
    }

    /// <summary>Bookmark with an /XYZ destination at the given XGraphics y (converted to PDF user space).</summary>
    private static PdfOutline AddBookmark(PdfOutlineCollection parent, string title, PdfPage page, double topDownY)
    {
        var outline = parent.Add(title, page, opened: true);
        outline.PageDestinationType = PdfPageDestinationType.Xyz;
        outline.Left = 0;
        outline.Top = PageHeight - topDownY;
        return outline;
    }

    private static void WriteCover(PdfPage page)
    {
        Write(page, 1, w =>
        {
            w.Centered("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", PageWriter.Heading2);
            w.Centered("Độc lập - Tự do - Hạnh phúc", PageWriter.BodyBold);
            w.Graphics.DrawLine(XPens.Black, 222, w.Y + 2, 373, w.Y + 2);
            w.Space(24);
            w.Centered("Hà Nội, ngày 15 tháng 01 năm 2026", new XFont("Times New Roman", 12, XFontStyleEx.Italic));
            w.Space(36);
            w.Centered("TÀI LIỆU MẪU KIỂM THỬ PDF_PRO", new XFont("Arial", 20, XFontStyleEx.Bold));
            w.Centered("Quy định giả định về quản lý tài liệu điện tử", PageWriter.Heading3);
            w.Space(24);
            w.Paragraph("Tài liệu này được sinh tự động để kiểm thử PDF_Pro: hiển thị, tìm kiếm, chọn chữ và dấu trang nhiều cấp. "
                + "Mọi thông tin trong tài liệu đều là dữ liệu giả, không liên quan đến người hay tổ chức có thật.");
            w.Heading("Bảng chữ có dấu", PageWriter.Heading3);
            w.Paragraph(AllLowercaseDiacritics);
            w.Paragraph(AllUppercaseDiacritics);
            w.Heading("Câu mẫu", PageWriter.Heading3);
            w.Paragraph("Thằng bé ngồi xổm bên bờ sông, nhặt từng viên sỏi ướt và đếm: một, hai, ba. Hòa, hóa, hỏa, họa, "
                + "Hoà và hoá là những cặp từ dùng để kiểm tra tìm kiếm có dấu và không dấu.");
        });
    }

    private static void Write(PdfPage page, int pageNumber, Action<PageWriter> content)
    {
        using var graphics = XGraphics.FromPdfPage(page);
        var writer = new PageWriter(graphics);
        content(writer);
        writer.Footer($"Trang {pageNumber}/{PageCount}", PageHeight);
    }

    private static void Filler(PageWriter writer, string label, int paragraphs)
    {
        for (var i = 1; i <= paragraphs; i++)
        {
            writer.Paragraph($"{label} {i}: Đơn vị lưu trữ kiểm tra danh mục hồ sơ hằng quý, đối chiếu bản giấy với bản điện tử "
                + "và ghi nhận sai lệch vào sổ theo dõi. Kết quả được báo cáo bằng văn bản, kèm đề xuất khắc phục cụ thể.");
        }
    }

    private static void DrawTable(PageWriter writer)
    {
        string[] header = ["STT", "Họ và tên", "Đơn vị", "Điện thoại nội bộ"];
        string[][] rows =
        [
            ["1", "Nguyễn Văn A", "Phòng Hành chính", "1001"],
            ["2", "Trần Thị B", "Phòng Kế toán", "1002"],
            ["3", "Lê Hoàng C", "Phòng Công nghệ", "1003"],
            ["4", "Phạm Thu D", "Phòng Lưu trữ", "1004"],
        ];
        double[] widths = [40, 170, 150, 91];
        var graphics = writer.Graphics;
        var y = writer.Y + 8;
        const double rowHeight = 24;
        foreach (var (cells, index) in new[] { header }.Concat(rows).Select((r, i) => (r, i)))
        {
            var x = 72.0;
            for (var c = 0; c < cells.Length; c++)
            {
                graphics.DrawRectangle(XPens.Black, x, y, widths[c], rowHeight);
                graphics.DrawString(
                    cells[c],
                    index == 0 ? PageWriter.BodyBold : PageWriter.Body,
                    XBrushes.Black,
                    new XRect(x + 4, y, widths[c] - 8, rowHeight),
                    XStringFormats.CenterLeft);
                x += widths[c];
            }

            y += rowHeight;
        }
    }
}

using PdfSharp.Drawing;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// contract-v1.pdf and contract-v2.pdf: a FAKE service contract in two versions for document comparison (SPEC P11).
/// Version 2 has one page inserted after page 2 (an appendix listing the services) and three places changed: the
/// price (page 1), the time to pay (page 3 of v1, 4 of v2) and the place of signing (last page). A comparison must
/// report exactly these four changes, and nothing on the pages that only moved.
/// </summary>
internal sealed class ContractSample(int version) : ISample
{
    public string FileName => $"contract-v{version}.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create($"Hợp đồng dịch vụ (phiên bản {version}, dữ liệu giả)", "So sánh hai phiên bản tài liệu");
        var pages = new List<string[]>
        {
            new[]
            {
                "#HỢP ĐỒNG CUNG CẤP DỊCH VỤ SỐ HOÁ TÀI LIỆU",
                "Số: 15/2026/HĐDV (dữ liệu giả định)",
                "Hôm nay, ngày 15 tháng 01 năm 2026, các bên gồm có:",
                "Bên A (bên sử dụng dịch vụ): Công ty Cổ phần Giả Định Mẫu Thử, địa chỉ số 12 đường Hoa Sữa, quận Ba Đình, đại diện là ông Trần Văn Kiểm, chức vụ Giám đốc.",
                "Bên B (bên cung cấp dịch vụ): Công ty TNHH Số Hoá Thử Nghiệm, địa chỉ số 34 đường Lê Lợi, quận Hải Châu, đại diện là bà Lê Thị Thử, chức vụ Giám đốc.",
                "*Điều 1. Nội dung dịch vụ",
                "Bên B nhận số hoá, nhận dạng chữ và sắp xếp hồ sơ lưu trữ của Bên A theo danh mục do Bên A cung cấp; tài liệu sau khi số hoá được giao ở định dạng PDF có lớp chữ tìm kiếm được.",
                "*Điều 2. Giá trị hợp đồng",
                version == 1
                    ? "Tổng giá trị hợp đồng là 100.000.000 đồng (một trăm triệu đồng), đã bao gồm thuế giá trị gia tăng và mọi chi phí vận chuyển tài liệu giữa hai bên."
                    : "Tổng giá trị hợp đồng là 120.000.000 đồng (một trăm hai mươi triệu đồng), đã bao gồm thuế giá trị gia tăng và mọi chi phí vận chuyển tài liệu giữa hai bên.",
            },
            new[]
            {
                "*Điều 3. Thời hạn thực hiện",
                "Bên B hoàn thành toàn bộ khối lượng công việc trong vòng chín mươi ngày kể từ ngày nhận đủ tài liệu gốc từ Bên A; mỗi đợt giao nộp được lập biên bản bàn giao có chữ ký của hai bên.",
                "*Điều 4. Quyền và nghĩa vụ của Bên A",
                "Bên A cung cấp tài liệu gốc đầy đủ, đúng danh mục; cử người phối hợp kiểm tra chất lượng bản số hoá; thanh toán đầy đủ và đúng hạn theo Điều 6.",
                "*Điều 5. Quyền và nghĩa vụ của Bên B",
                "Bên B bảo quản tài liệu gốc trong suốt thời gian thực hiện, không sao chép hay tiết lộ nội dung cho bên thứ ba; bản số hoá phải đọc được, đủ trang và đúng thứ tự.",
            },
            new[]
            {
                "*Điều 6. Thanh toán",
                version == 1
                    ? "Bên A thanh toán cho Bên B bằng chuyển khoản trong vòng 30 ngày kể từ ngày hai bên ký biên bản nghiệm thu cuối cùng."
                    : "Bên A thanh toán cho Bên B bằng chuyển khoản trong vòng 45 ngày kể từ ngày hai bên ký biên bản nghiệm thu cuối cùng.",
                "*Điều 7. Bảo mật thông tin",
                "Mọi thông tin trong tài liệu của Bên A là thông tin mật; Bên B chịu trách nhiệm về việc để lộ thông tin do lỗi của nhân viên mình trong và sau thời hạn hợp đồng.",
                "*Điều 8. Giải quyết tranh chấp",
                "Tranh chấp phát sinh được giải quyết trước hết bằng thương lượng; nếu không thương lượng được trong ba mươi ngày, một bên có quyền đưa ra toà án có thẩm quyền.",
            },
            new[]
            {
                "*Điều 9. Hiệu lực hợp đồng",
                "Hợp đồng có hiệu lực từ ngày ký và tự thanh lý khi hai bên hoàn thành nghĩa vụ. Hợp đồng lập thành bốn bản có giá trị như nhau, mỗi bên giữ hai bản.",
                version == 1 ? "Ký tại Hà Nội, ngày 15 tháng 01 năm 2026." : "Ký tại Đà Nẵng, ngày 15 tháng 01 năm 2026.",
                "ĐẠI DIỆN BÊN A                                        ĐẠI DIỆN BÊN B",
                "Trần Văn Kiểm                                          Lê Thị Thử",
            },
        };

        if (version == 2)
        {
            pages.Insert(2, new[]
            {
                "#PHỤ LỤC 1 — DANH MỤC DỊCH VỤ",
                "Phụ lục này là một phần không tách rời của hợp đồng.",
                "1. Số hoá tài liệu khổ A4 và A3, độ phân giải 300 điểm ảnh mỗi inch.",
                "2. Nhận dạng chữ tiếng Việt và tạo lớp chữ tìm kiếm được cho mọi trang.",
                "3. Đặt tên tệp và lập mục lục theo hồ sơ, đánh số trang liên tục.",
                "4. Kiểm tra chất lượng từng đợt và bàn giao trên ổ cứng có mã hoá.",
            });
        }

        for (var p = 0; p < pages.Count; p++)
        {
            var page = SampleDocument.AddA4Page(document);
            using var graphics = XGraphics.FromPdfPage(page);
            var writer = new PageWriter(graphics);
            foreach (var line in pages[p])
            {
                if (line.StartsWith('#'))
                {
                    writer.Heading(line[1..], PageWriter.Heading1);
                }
                else if (line.StartsWith('*'))
                {
                    writer.Heading(line[1..], PageWriter.Heading2);
                }
                else
                {
                    writer.Paragraph(line);
                }
            }

            writer.Footer($"Trang {p + 1}/{pages.Count}", 842);
        }

        document.Save(path);
    }
}

namespace PdfPro.SampleGenerator.Samples;

/// <summary>One fake administrative document: who issued it, its number, kind and pages of text.</summary>
/// <param name="Issuer">Issuing body (two lines, upper case).</param>
/// <param name="Number">"Số: …" line of the first page.</param>
/// <param name="Kind">Document kind in upper case ("QUYẾT ĐỊNH").</param>
/// <param name="Subject">One-line subject under the kind.</param>
/// <param name="Bookmark">Title of its bookmark in vanban-vi.pdf.</param>
/// <param name="Pages">Paragraphs of each page.</param>
internal sealed record FakeLegalDocument(
    string[] Issuer,
    string Number,
    string Kind,
    string Subject,
    string Bookmark,
    string[][] Pages);

/// <summary>
/// A bundle of five FAKE Vietnamese administrative documents (decision, official letter, submission, minutes,
/// contract) used by vanban-vi.pdf (with text) and scan-vi.pdf (scanned images). Every name, number and place is
/// invented. Only first pages carry a "Số:" line, so a text split on "Số:\s*\d+" finds exactly the documents.
/// </summary>
internal static class LegalBundle
{
    public const string Nation = "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM";
    public const string Motto = "Độc lập - Tự do - Hạnh phúc";
    public const string Place = "Xã Mẫu Thử, ngày 15 tháng 01 năm 2026";

    public static readonly FakeLegalDocument[] Documents =
    [
        new(
            ["ỦY BAN NHÂN DÂN", "XÃ MẪU THỬ"],
            "Số: 12/QĐ-UBND",
            "QUYẾT ĐỊNH",
            "Về việc thành lập Tổ kiểm thử phần mềm xử lý tài liệu",
            "Quyết định số 12/QĐ-UBND",
            [
                [
                    "CHỦ TỊCH ỦY BAN NHÂN DÂN XÃ MẪU THỬ",
                    "Căn cứ quy chế làm việc giả định của Ủy ban nhân dân xã Mẫu Thử; xét đề nghị của Trưởng phòng Văn hóa - Thông tin giả định;",
                    "QUYẾT ĐỊNH:",
                    "Điều 1. Thành lập Tổ kiểm thử phần mềm xử lý tài liệu gồm các thành viên có tên trong danh sách kèm theo. Tổ có nhiệm vụ kiểm tra việc tách, gộp và sắp xếp trang của các văn bản mẫu.",
                ],
                [
                    "Điều 2. Tổ kiểm thử làm việc theo chế độ kiêm nhiệm, tự giải thể sau khi hoàn thành nhiệm vụ. Kinh phí hoạt động được bố trí từ nguồn ngân sách giả định của xã.",
                    "Điều 3. Văn phòng Ủy ban nhân dân xã, các ông (bà) có tên tại Điều 1 chịu trách nhiệm thi hành Quyết định này. Quyết định có hiệu lực kể từ ngày ký.",
                ],
                [
                    "DANH SÁCH THÀNH VIÊN TỔ KIỂM THỬ (dữ liệu giả)",
                    "1. Ông Nguyễn Văn Mẫu - Tổ trưởng. 2. Bà Trần Thị Thử - Tổ phó. 3. Ông Lê Văn Nghiệm - Thành viên. 4. Bà Phạm Thị Thu - Thành viên.",
                    "Nơi nhận: Như Điều 3; Lưu văn thư.",
                ],
            ]),
        new(
            ["SỞ THÔNG TIN MẪU", "VĂN PHÒNG"],
            "Số: 45/STTM-VP",
            "CÔNG VĂN",
            "V/v góp ý dự thảo quy chế quản lý văn bản điện tử",
            "Công văn 45/STTM-VP",
            [
                [
                    "Kính gửi: Các phòng, ban chuyên môn thuộc Sở.",
                    "Thực hiện kế hoạch giả định về chuyển đổi số, Văn phòng Sở đề nghị các đơn vị nghiên cứu, góp ý dự thảo quy chế quản lý văn bản điện tử gửi kèm công văn này.",
                ],
                [
                    "Ý kiến góp ý gửi về Văn phòng Sở trước ngày 30 tháng 01 năm 2026 để tổng hợp. Quá thời hạn trên, đơn vị không có ý kiến được hiểu là thống nhất với dự thảo.",
                    "Nơi nhận: Như trên; Lưu VT.",
                ],
            ]),
        new(
            ["PHÒNG TÀI CHÍNH MẪU", "BỘ PHẬN KẾ HOẠCH"],
            "Số: 07/TTr-PTC",
            "TỜ TRÌNH",
            "Đề nghị phê duyệt dự toán kinh phí mua sắm thiết bị",
            "Tờ trình 07/TTr-PTC",
            [
                [
                    "Kính gửi: Lãnh đạo đơn vị (giả định).",
                    "Phòng Tài chính kính trình phê duyệt dự toán kinh phí mua sắm máy quét tài liệu phục vụ số hóa hồ sơ lưu trữ, tổng kinh phí dự kiến một trăm hai mươi triệu đồng (số liệu giả).",
                ],
                [
                    "Kính trình Lãnh đạo xem xét, phê duyệt để Phòng triển khai thực hiện theo đúng quy định.",
                    "Nơi nhận: Như trên; Lưu PTC.",
                ],
            ]),
        new(
            ["HỘI ĐỒNG NGHIỆM THU MẪU", "TỔ THƯ KÝ"],
            "Số: 03/BB-HĐNT",
            "BIÊN BẢN",
            "Nghiệm thu phần mềm xử lý tài liệu PDF_Pro",
            "Biên bản nghiệm thu 03/BB-HĐNT",
            [
                [
                    "Hôm nay, tại trụ sở giả định, Hội đồng nghiệm thu tiến hành kiểm tra phần mềm xử lý tài liệu theo hợp đồng đã ký.",
                    "Thành phần gồm đại diện bên giao và bên nhận (tên người là dữ liệu giả).",
                ],
                [
                    "Kết quả kiểm tra: các chức năng xem, sắp xếp, tách và gộp trang hoạt động đúng yêu cầu. Tài liệu tiếng Việt hiển thị đầy đủ dấu.",
                ],
                [
                    "Kết luận: Hội đồng thống nhất nghiệm thu, đề nghị đưa phần mềm vào sử dụng. Biên bản lập thành hai bản có giá trị như nhau.",
                ],
            ]),
        new(
            ["CÔNG TY TNHH MẪU THỬ", "PHÒNG KINH DOANH"],
            "Số: 128/2026/HĐ-MT",
            "HỢP ĐỒNG",
            "Cung cấp dịch vụ số hóa tài liệu",
            "Hợp đồng 128/2026/HĐ-MT",
            [
                [
                    "Bên A: Công ty TNHH Mẫu Thử (giả định). Bên B: Hộ kinh doanh Dịch Vụ Thử (giả định).",
                    "Hai bên thỏa thuận ký kết hợp đồng cung cấp dịch vụ số hóa tài liệu với các điều khoản sau.",
                ],
                [
                    "Điều 1. Nội dung công việc: Bên B quét, nhận dạng và tách các bộ hồ sơ giấy thành tệp PDF theo từng văn bản.",
                ],
                [
                    "Điều 2. Thời gian thực hiện: ba mươi ngày kể từ ngày ký. Điều 3. Giá trị hợp đồng và phương thức thanh toán theo phụ lục (số liệu giả).",
                ],
                [
                    "Điều 4. Hợp đồng lập thành bốn bản, mỗi bên giữ hai bản có giá trị pháp lý như nhau.",
                    "ĐẠI DIỆN BÊN A                                        ĐẠI DIỆN BÊN B",
                ],
            ]),
    ];
}

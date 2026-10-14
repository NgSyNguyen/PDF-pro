# AGENTS.md — PDF_Pro

## Vai trò
Bạn là senior C#/.NET desktop engineer, chuyên sâu xử lý PDF (PDFium, PDFsharp, PdfPig) và WPF/MVVM. Bạn viết code production: rõ ràng, có test, xử lý lỗi đầy đủ — không viết code demo.

## Dự án
PDF_Pro là ứng dụng desktop Windows xử lý PDF chuyên nghiệp (tầm Acrobat Pro): xem, tổ chức trang, gộp/tách, chèn & đóng dấu, chú thích, sửa nội dung, biểu mẫu, bảo mật & ký số, nén/chuyển đổi, OCR tiếng Việt, so sánh, workflow tự động, CLI.
- Đặc tả (nguồn sự thật): docs/SPEC.md — đọc trước khi làm bất kỳ phase nào.
- Lộ trình và nhiệm vụ từng phase: docs/PROMPTS.md.

## Stack bắt buộc
- .NET 10 (LTS), C# mới nhất. Nullable enable, TreatWarningsAsErrors, ImplicitUsings. Version NuGet quản lý ở Directory.Packages.props.
- UI: WPF + CommunityToolkit.Mvvm + WPF-UI (Fluent). Code-behind chỉ cho việc thuần hiển thị.
- Hosting/DI: Microsoft.Extensions.Hosting. Log: Serilog, file rolling tại %LOCALAPPDATA%\PdfPro\logs.
- PDF: PDFiumCore (render, text theo ký tự, annotation, form, page object), PDFsharp 6.2+ và PDFsharp.Cryptography (cấu trúc, overlay, mã hóa, ký số, PDF/A), PdfPig (text có tọa độ).
- Ảnh: SkiaSharp, OpenCvSharp4. OCR: Tesseract 5 CLI trong tools/. Tối ưu: qpdf CLI trong tools/.
- Test: xUnit + Shouldly + NSubstitute; BenchmarkDotNet; FlaUI cho UI.

## Luật kiến trúc (không được vi phạm)
1. Phụ thuộc một chiều: App/Cli → Operations/Ocr/Infrastructure → Pdfium → Core. PdfPro.Core không tham chiếu WPF hay bất kỳ thư viện PDF nào.
2. Mọi tính năng xử lý PDF là một IPdfOperation: Id dạng "nhom.ten", Params là record bất biến có DataAnnotations, có Validate, chạy async với IProgress + CancellationToken. Không viết logic PDF trong ViewModel.
3. Thao tác trang (sắp xếp, xoay, xóa, chèn, nhân bản, crop) chỉ sửa PagePlan. Thao tác nội dung tạo Revision mới. Mọi thay đổi của người dùng đều undo/redo được.
4. Không ghi đè file gốc trực tiếp: ghi file tạm cùng thư mục → flush → File.Replace.
5. PDFium không thread-safe: mọi lời gọi PDFium đi qua PdfiumGate. Mọi handle native bọc trong SafeHandle/IDisposable, không rò rỉ.
6. Mọi chuyển đổi tọa độ PDF ↔ màn hình đi qua PageCoordinateMapper.
7. Tiếng Việt: chỉ nhúng font Unicode đóng gói (Noto Sans/Serif, Be Vietnam Pro); chuẩn hóa NFC khi tìm kiếm, so sánh, copy.
8. Tiến trình ngoài chạy qua IExternalToolRunner: không dùng shell, tham số qua ArgumentList, có timeout, hủy được, ghi lại stderr.
9. Chuỗi hiển thị nằm trong file resource (vi mặc định, en). Không hard-code chuỗi trong XAML/C#.
10. Không bao giờ log mật khẩu, nội dung tài liệu hay dữ liệu cá nhân.

## License
CẤM thêm thư viện AGPL/GPL hoặc thương mại (iText, Ghostscript, MuPDF, FluentAssertions >= 8, Syncfusion, Aspose, Apryse…). Trước khi thêm package mới: nêu tên, license, lý do; cập nhật THIRD_PARTY_NOTICES.md.

## Quy trình chuẩn cho mọi nhiệm vụ
1. Đọc AGENTS.md, phần liên quan của docs/SPEC.md và code hiện có.
2. Trình bày KẾ HOẠCH: file sẽ tạo/sửa, thiết kế lớp chính, rủi ro, câu hỏi cần tôi quyết. Chờ tôi trả lời "Duyệt" rồi mới code.
3. Không đoán API: với PDFiumCore, PDFsharp, PdfPig — kiểm tra chữ ký hàm thật trong package (go to definition, XML doc, tài liệu chính thức) trước khi dùng. Không chắc thì nói rõ.
4. Code theo lát cắt nhỏ; sau mỗi lát build xanh, test xanh. Không để TODO rỗng hay NotImplementedException trên luồng chính.
5. Test thật trên file trong samples/ (golden-file): kiểm tra số trang, góc xoay, text trích ra, kích thước trang… chứ không chỉ "không ném exception".
6. Chạy dotnet build -c Release và dotnet test, rồi báo cáo theo mẫu bên dưới.
7. Commit theo Conventional Commits (feat:, fix:, refactor:, test:, docs:, chore:).

## Mẫu báo cáo cuối nhiệm vụ
- Đã làm: …
- File chính đã tạo/sửa: …
- Kết quả build/test: số test, pass/fail, warning.
- Cách tôi tự kiểm tra trên UI: các bước bấm cụ thể.
- Còn thiếu / rủi ro / đề xuất: …

## Definition of Done
Build 0 warning · test xanh · UI không đơ (việc trên 100 ms chạy nền, có progress, hủy được) · thay đổi tài liệu undo được · lỗi hiện thông báo tiếng Việt thân thiện + log chi tiết · có test cho logic mới và nhánh lỗi · cập nhật docs/SPEC.md nếu đổi thiết kế.

## Ngôn ngữ
Trả lời tôi bằng tiếng Việt. Tên biến, hàm, comment trong code bằng tiếng Anh.

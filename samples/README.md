# samples/ — tài liệu mẫu để kiểm thử

Mọi tệp ở đây là **dữ liệu giả**, được sinh bởi `tests/PdfPro.SampleGenerator` (PDFsharp 6.2.4 + font Arial/Times New Roman của Windows; `scan-vi.pdf` được PDFium render từ `vanban-vi.pdf` rồi WPF nén JPEG). Không đưa tài liệu thật vào thư mục này — nó được commit lên git.

Sinh lại (chỉ cần khi sửa bộ sinh; mỗi lần sinh khác byte vì `/ID` ngẫu nhiên nhưng nội dung giống hệt):

```powershell
dotnet run --project tests/PdfPro.SampleGenerator
```

| Tệp | Nội dung | Dùng để test |
|---|---|---|
| `basic-vi.pdf` | 6 trang A4 tiếng Việt (đủ mọi chữ có dấu), bookmark 3 cấp có vị trí `/XYZ`, metadata tiếng Việt, bảng ở phụ lục | Hiển thị, text, bookmark, metadata, ảnh tham chiếu render |
| `big-1000.pdf` | 1.000 trang A4, mỗi trang có chữ, hình vector và 1 trong 5 ảnh dùng chung; bookmark mỗi 100 trang | Hiệu năng, bộ nhớ, ảo hóa |
| `encrypted.pdf` | AES-256, mật khẩu mở `test123` (mật khẩu chủ giả `chu-so-huu-456`), cấm in và sao chép | Mật khẩu, quyền, gộp tệp có mật khẩu (P4) |
| `broken.pdf` | Tài liệu 3 trang bị cắt bỏ bảng xref + trailer ở cuối (các object vẫn còn) | Tệp hỏng, sửa tệp (P9) |
| `links-annots.pdf` | 6 trang A4 ("Trang 1"…"Trang 6"): trang 1 có link tới trang 4 (`/Dest` trực tiếp), tới trang 6 (đích có tên `phu-luc` qua action GoTo), tới trang 3 (action GoTo) và link web `example.com`; trang 3 link về trang 1; trang 2 có ghi chú dán và vùng tô sáng; bookmark, nhãn trang (i, 1–5), `/OpenAction` | Giữ link, chú thích, bookmark khi sắp xếp / xóa / nhân bản / chèn trang (P3) |
| `rotated-cropbox.pdf` | 4 trang `/Rotate` 0/90/180/270, CropBox hoặc MediaBox không bắt đầu từ (0,0); ô màu đánh dấu 4 góc vùng hiển thị (đỏ = trên-trái, xanh lá = trên-phải, xanh dương = dưới-trái, đen = dưới-phải) | Hệ tọa độ, xoay trang |
| `vanban-vi.pdf` | 14 trang A4: 5 văn bản hành chính GIẢ (quyết định, công văn, tờ trình, biên bản, hợp đồng) liền nhau; trang đầu mỗi văn bản có quốc hiệu và dòng "Số: …" (trang 1, 4, 6, 8, 11), các trang sau không có; mỗi văn bản một bookmark cấp 1 có dấu và ký tự "/" | Tách theo văn bản (regex), theo bookmark tiếng Việt, đặt tên tệp (P4) |
| `scan-vi.pdf` | 18 trang ảnh JPEG xám 90 DPI giả lập bản scan `vanban-vi.pdf` (không có lớp chữ): nền giấy, bụi, trang hơi nghiêng, trang 12 lộn ngược; tờ trắng phân cách ở trang 4, 7, 10, 14 (trang 7 có chữ thấm mờ từ mặt sau); văn bản bắt đầu ở trang 1, 5, 8, 11, 15 | Tách theo trang đánh dấu, theo trang trắng phân cách (P4); OCR, chỉnh nghiêng, nén (P10) |

| `pii.pdf` | 2 trang A4 dữ liệu cá nhân GIẢ: số CCCD, điện thoại di động/cố định, email, số tài khoản, ngày sinh; dữ liệu còn nằm ở tác giả + từ khóa của thông tin tài liệu, tên bookmark (có mục con), một ghi chú dán và một liên kết `mailto:`. Lần đầu sinh trên Linux với Noto Sans/Serif thay Arial/Times (chương trình tạm dùng `PiiSample` của bộ sinh); sinh lại trên Windows ra Arial/Times với cùng nội dung | Xóa vĩnh viễn theo mẫu, dọn metadata/bookmark/chú thích/liên kết (P8) |

Các tệp còn lại trong SPEC (form, chữ ký số, bảng, so sánh, annotation) được bổ sung ở các phase cần đến chúng.

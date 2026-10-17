# samples/ — tài liệu mẫu để kiểm thử

Mọi tệp ở đây là **dữ liệu giả**, được sinh bởi `tests/PdfPro.SampleGenerator` (PDFsharp 6.2.4 + font Arial/Times New Roman của Windows). Không đưa tài liệu thật vào thư mục này — nó được commit lên git.

Sinh lại (chỉ cần khi sửa bộ sinh; mỗi lần sinh khác byte vì `/ID` ngẫu nhiên nhưng nội dung giống hệt):

```powershell
dotnet run --project tests/PdfPro.SampleGenerator
```

| Tệp | Nội dung | Dùng để test |
|---|---|---|
| `basic-vi.pdf` | 6 trang A4 tiếng Việt (đủ mọi chữ có dấu), bookmark 3 cấp có vị trí `/XYZ`, metadata tiếng Việt, bảng ở phụ lục | Hiển thị, text, bookmark, metadata, ảnh tham chiếu render |
| `big-1000.pdf` | 1.000 trang A4, mỗi trang có chữ, hình vector và 1 trong 5 ảnh dùng chung; bookmark mỗi 100 trang | Hiệu năng, bộ nhớ, ảo hóa |
| `encrypted.pdf` | AES-256, mật khẩu mở `test123`, cấm in và sao chép | Mật khẩu, quyền |
| `broken.pdf` | Tài liệu 3 trang bị cắt bỏ bảng xref + trailer ở cuối (các object vẫn còn) | Tệp hỏng, sửa tệp (P9) |
| `rotated-cropbox.pdf` | 4 trang `/Rotate` 0/90/180/270, CropBox hoặc MediaBox không bắt đầu từ (0,0); ô màu đánh dấu 4 góc vùng hiển thị (đỏ = trên-trái, xanh lá = trên-phải, xanh dương = dưới-trái, đen = dưới-phải) | Hệ tọa độ, xoay trang |

Các tệp còn lại trong SPEC (scan, form, chữ ký số, bảng, so sánh, annotation, PII) được bổ sung ở các phase cần đến chúng.

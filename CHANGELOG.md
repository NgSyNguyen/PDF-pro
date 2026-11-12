# Nhật ký thay đổi

Mọi thay đổi đáng kể của PDF_Pro. Định dạng theo [Keep a Changelog](https://keepachangelog.com/vi/1.1.0/), phiên bản
theo [Semantic Versioning](https://semver.org/lang/vi/). Mục đầu tiên là ghi chú phát hành của bộ cài.

## [1.0.0] — chưa phát hành

Phiên bản đầu tiên.

### Xem và tổ chức
- Trình xem PDF nhanh (PDFium): thu phóng, cuộn liên tục, trang đôi, thumbnail, bookmark, in; mở nhiều tài liệu theo
  tab, kéo thả tệp, khôi phục thay đổi chưa lưu sau sự cố.
- Sắp xếp trang: kéo thả, xoay, xóa, nhân bản, chèn trang trống / từ tệp, trích, cắt lề, đổi khổ — mọi thay đổi hoàn
  tác được; tệp gốc chỉ được ghi khi lưu.
- Gộp, tách (theo số trang, dung lượng, bookmark, trang trắng, dấu tách), xen trang hai mặt, N-up, sổ gập, chia ô.

### Chỉnh sửa
- Chèn chữ, ảnh, hình, chữ ký tay; hình mờ, nền, đầu/chân trang, số trang, Bates nhiều tệp — tiếng Việt đúng dấu với
  phông đóng gói (Noto, Be Vietnam Pro).
- Chọn và chép chữ, tìm kiếm tiếng Việt có/không dấu; chú thích đầy đủ (tô sáng, ghi chú, hộp chữ, vẽ tay, hình, con
  dấu), panel bình luận, làm phẳng.
- Sửa chữ và ảnh có sẵn trong trang; biểu mẫu: điền, thiết kế, nhận diện trường tự động, nhập/xuất dữ liệu, trộn từ
  CSV/Excel, làm phẳng.

### Bảo mật
- Mật khẩu và quyền (AES-256); xóa vĩnh viễn thông tin (CCCD, số điện thoại, email… hoặc tự đánh dấu) có kiểm lại;
  làm sạch tài liệu; ký số (chứng thư Windows, .pfx, USB token, dấu thời gian), ký nhiều tệp, kiểm tra chữ ký.

### Tối ưu và chuyển đổi
- Nén theo độ phân giải hiển thị thật, tối ưu cho web, sửa tệp hỏng, thang xám, PDF/A-2b/1b (kiểm bằng veraPDF nếu
  có); sang ảnh (PNG, JPEG, TIFF nhiều trang), ảnh sang PDF, sang văn bản/Markdown, Office sang PDF (LibreOffice).
- OCR tiếng Việt (Tesseract) tạo lớp chữ ẩn; công cụ bản quét: xóa trang trắng, xoay đúng chiều, làm thẳng, xóa viền đen.
- So sánh hai tài liệu (chữ và hình ảnh, báo cáo PDF); trích bảng sang Excel/CSV, trích ảnh, tệp đính kèm, phông chữ;
  sửa bookmark, thuộc tính, nhãn trang, liên kết.

### Tự động hóa
- Tìm lệnh (Ctrl+K), workflow (`.pdfflow`), chạy lô, thư mục theo dõi (hot folder), dòng lệnh `pdfpro`.

### Hoàn thiện
- Tệp độc hại hoặc hỏng nặng không làm sập ứng dụng; thao tác nặng chạy trong tiến trình riêng có giới hạn bộ nhớ.
- Bảng phím tắt (F1), hướng dẫn nhanh, hỗ trợ trình đọc màn hình và tương phản cao, Giới thiệu (giấy phép), sao chép
  thông tin lỗi; bộ cài và tự cập nhật (kênh ổn định/thử nghiệm). Không thu thập dữ liệu sử dụng.

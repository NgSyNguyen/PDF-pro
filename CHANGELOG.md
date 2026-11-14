# Nhật ký thay đổi

Mọi thay đổi đáng kể của PDF_Pro. Định dạng theo [Keep a Changelog](https://keepachangelog.com/vi/1.1.0/), phiên bản
theo [Semantic Versioning](https://semver.org/lang/vi/). Mục đầu tiên là ghi chú phát hành của bộ cài.

## [Chưa phát hành]

- **Excel, PowerPoint sang PDF bằng Microsoft Office hoặc WPS Office có trên máy** (như Word): máy không có LibreOffice
  vẫn chuyển được bảng tính, bản trình chiếu.

## [1.0.0-beta.3] — 14/11/2026 (bản thử nghiệm, không phát hành được: một test phụ thuộc số phiên bản)

- **Word sang PDF không cần cài thêm gì** khi máy có Microsoft Word hoặc WPS Office: tài liệu Word được chính trình soạn
  thảo đó chuyển (dàn trang đúng như khi mở bằng Word); bảng tính, bản trình chiếu — và tài liệu Word khi không có hai
  chương trình đó — vẫn dùng LibreOffice. Mở thẳng tệp Word (Ctrl+O, kéo thả vào cửa sổ) là có bản PDF cạnh
  nó và mở ngay; nút "Chuyển Word sang PDF" ở màn hình chính.
- Sửa các biểu tượng hiện thành chữ lạ trên ribbon (Từ Office, Liên kết, Tối ưu cho web, Workflow, Xuất dữ liệu biểu mẫu).
- Bản thử nghiệm cài trên máy mới tự theo kênh thử nghiệm (trước đây theo kênh ổn định nên không thấy bản beta sau).

## [1.0.0-beta.2] — 13/11/2026 (bản thử nghiệm)

Bản thử nghiệm thứ hai: như 1.0.0-beta.1, phát hành lại qua GitHub (bản beta.1 đã cài tự cập nhật lên bản này khi đóng
ứng dụng). Quy trình phát hành giờ ghi rõ bước nào lỗi.

## [1.0.0-beta.1] — 13/11/2026 (bản thử nghiệm)

Bản phát hành thử đầu tiên (kênh thử nghiệm): mọi tính năng của 1.0.0 dưới đây, cộng thêm:

- Chữ ký chứng nhận (DocMDP), chữ ký chuẩn PAdES, dấu thời gian, dữ liệu xác minh lâu dài (LTV: chứng thư, OCSP, CRL)
  và dấu thời gian tài liệu (PAdES-B-LTA); điền biểu mẫu trên tệp đã ký vẫn giữ chữ ký.
- Bản PDF/A-2b và PDF/A-1b đạt kiểm định veraPDF trên mọi tệp mẫu; Office sang PDF chạy được cả khi lưu vào thư mục sâu.
- Thao tác nặng — OCR, nén, chuyển đổi, công cụ bản quét, trích bảng/ảnh, so sánh, chạy lô, hot folder, workflow — chạy
  trong tiến trình riêng: tệp lạ làm sập nó không làm sập cửa sổ.
- Chế độ an toàn: tệp đang mở khi ứng dụng dừng đột ngột được thử trong tiến trình riêng trước khi mở lại.
- "Gộp bằng PDF_Pro" trong menu chuột phải của Explorer; vẽ liên kết lên trang; tài liệu có mật khẩu giữ bảo vệ khi
  trích/tách trang; OCR lại các trang thành ảnh sau khi xóa vĩnh viễn.

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

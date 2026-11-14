# Phát hành PDF_Pro

> Tài liệu cho người phát hành (SPEC Phase 13). Mọi bước chạy trên GitHub Actions: `.github/workflows/release.yml`.

## Phát hành một phiên bản

1. Cập nhật `CHANGELOG.md` (mục mới ở đầu, đúng số phiên bản) — nội dung này thành ghi chú phát hành và hiện trong bộ cài.
2. Gắn thẻ và đẩy:
   ```powershell
   git tag v1.0.0
   git push origin v1.0.0
   ```
   - `v1.2.3` → kênh **ổn định** (`win`, `win-arm64`); `v1.2.3-beta.1` → kênh **thử nghiệm** (`beta`, `beta-arm64`), bản phát hành đánh dấu *pre-release*.
   - Hoặc chạy tay: Actions › release › *Run workflow*, nhập phiên bản.
3. Workflow làm lần lượt:
   - tải qpdf + Tesseract + tessdata (`tools/fetch-tools.ps1`);
   - build Release (cảnh báo là lỗi) với `-p:Version=…`, chạy toàn bộ test;
   - `dotnet publish` **self-contained** `win-x64` và `win-arm64`, **ReadyToRun**, không trimming (WPF dùng nhiều reflection) — `PdfPro.exe` và `PdfPro.Worker.exe` dùng chung runtime đóng kèm;
   - `pdfpro` (CLI) publish riêng thành `pdfpro-<phiên bản>-<rid>.zip` (trên Windows `pdfpro.exe` và `PdfPro.exe` là cùng một tên);
   - `vpk download github` (bản trước, để tạo gói delta) → `vpk pack` (Setup.exe, bản portable, gói full + delta, ký nếu có chứng thư) → `vpk upload github --publish` → đính kèm zip CLI.

Bản cài đặt vào `%LocalAppData%\PDF_Pro` của người dùng (packId `PDF_Pro`; không cần quyền quản trị), tạo lối tắt Start
Menu/Desktop, và đăng ký PDF_Pro trong **"Mở bằng"** của tệp `.pdf` (không tự làm ứng dụng mặc định — người dùng chọn
trong Windows). Gỡ cài đặt xóa cả thư mục cài, các khóa registry đó và menu chuột phải. **Dữ liệu người dùng nằm ở thư
mục khác** (`%LOCALAPPDATA%\PdfPro`: cài đặt, nhật ký, phiên chưa lưu) và được giữ — vì vậy packId **không được** là
`PdfPro` (Velopack xóa nguyên thư mục cài khi gỡ).

## Ký mã (code signing)

Workflow ký `Setup.exe` và các exe khi kho có hai **secret** (Settings › Secrets and variables › Actions):

| Secret | Giá trị |
|---|---|
| `SIGNING_CERTIFICATE_PFX` | Chứng thư ký mã dạng `.pfx`, mã hóa **base64** (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))`) |
| `SIGNING_CERTIFICATE_PASSWORD` | Mật khẩu của tệp `.pfx` |

`vpk pack --signParams "/f cert.pfx /p … /fd SHA256 /tr http://timestamp.digicert.com /td SHA256"` gọi `signtool` cho mọi
tệp thực thi. Không có secret thì bản phát hành không được ký (Windows SmartScreen sẽ cảnh báo). Chứng thư EV/HSM trên
đám mây (Azure Trusted Signing…): thay `--signParams` bằng `--azureTrustedSignFile` của `vpk` theo tài liệu Velopack.
Tệp `.pfx` được xóa khỏi máy chạy ở bước cuối (`if: always()`).

## Cập nhật trong ứng dụng

- Bản đã cài (`UpdateManager.IsInstalled`) **20 giây sau khi mở** hỏi trang phát hành GitHub xem có bản mới không
  (Cài đặt › "Tự kiểm tra cập nhật", mặc định bật; chỉ gửi yêu cầu HTTP, không gửi dữ liệu nào). Có bản mới → hỏi "Tải
  về"; tải ở thanh trạng thái (gói delta khi được); **cài khi người dùng đóng PDF_Pro** (`WaitExitThenApplyUpdates`) — không
  bao giờ khởi động lại giữa lúc đang làm việc.
- Giới thiệu › "Kiểm tra cập nhật" kiểm ngay và báo mọi kết quả (đã mới nhất / không có mạng / bản này không cài bằng
  bộ cài).
- Kênh: Cài đặt › Kênh "Ổn định" / "Thử nghiệm (beta)"; máy ARM64 tự dùng kênh `-arm64`. Chưa chọn kênh (`updateChannel` trống) thì theo phiên bản đang chạy: `1.2.3-beta.n` → thử nghiệm, `1.2.3` → ổn định (`UpdateChannels.Resolve`).

## Kiểm tra trước khi phát hành (máy Windows)

1. Máy ảo Windows sạch (chưa có .NET): chạy `Setup.exe` → PDF_Pro mở được, mở/lưu/OCR một tệp, "Mở bằng" có PDF_Pro.
2. Cập nhật tự động: cài `1.0.0`, phát hành `1.0.1`, mở `1.0.0` → sau ~20 s được hỏi → Tải về → đóng app → mở lại là `1.0.1`
   (Giới thiệu hiện phiên bản).
3. Gỡ cài đặt: không còn tệp ngoài `%LOCALAPPDATA%\PdfPro` (dữ liệu người dùng), không còn khóa `HKCU\Software\Classes\PdfPro.Document`.
4. `THIRD_PARTY_NOTICES.md` và thư mục `licenses`: không có thư viện AGPL/GPL (test `LicenseGuardTests` canh PDFsharp).
5. Chạy tay các `Phase*AcceptanceTests` (`$env:PDFPRO_UI_TESTS=1`).

## Biểu tượng

`assets/icon/PdfPro.ico` (tự vẽ bằng SkiaSharp: trang giấy gấp góc, vài dòng chữ, dải đỏ chữ "PDF" bằng Noto Sans Bold;
mỗi cỡ 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 vẽ riêng, cạnh khớp điểm ảnh — dưới 24 px dải đỏ có một vạch trắng thay
chữ) là biểu tượng của `PdfPro.exe` (`ApplicationIcon`: cửa sổ, thanh tác vụ, khay hệ thống, lối tắt, "Mở bằng"), của
`pdfpro.exe` và của bộ cài (`vpk pack --icon`). `PdfPro-256.png` là bản xem trước.

## Lịch sử phát hành thử

- `v1.0.0-beta.1` (13/11/2026): workflow hỏng ở bước Velopack (log cần đăng nhập mới đọc được) → không có bản trên GitHub; bộ cài beta.1 được đóng gói trên máy chủ dự án (cùng tham số `vpk pack`) để cài thử trên máy khác. Workflow giờ đưa phần cuối output của lệnh `vpk`/`gh` lỗi vào annotation của lần chạy.
- `v1.0.0-beta.2` (13/11/2026): **phát hành được** (run 43460744492 xanh, mọi bước): pre-release có `PDF_Pro-beta-Setup.exe`, `PDF_Pro-beta-arm64-Setup.exe`, bản portable, gói `-full.nupkg`, `releases.beta*.json` và `pdfpro-…-win-x64/arm64.zip`. Khác beta.1: `vpk` gọi bằng đường dẫn đầy đủ (`%USERPROFILE%\.dotnet\tools\vpk.exe` — công cụ vừa cài bằng `dotnet tool install -g` chưa có trên PATH của chính bước đó, nhiều khả năng là lý do beta.1 hỏng). Bản beta.1 đã cài **chỉ thấy bản này khi chọn kênh "Thử nghiệm (beta)" trong Cài đặt** (beta.1 mặc định kênh ổn định); từ bản sau, chưa chọn kênh thì bản beta theo kênh thử nghiệm.
- `v1.0.0-beta.3` (14/11/2026, thẻ trên `9e13bd0`): Word sang PDF bằng Microsoft Word/WPS có trên máy, mở thẳng tệp Word, nút "Chuyển Word sang PDF", sửa biểu tượng hiện thành chữ lạ; bản cài trên máy mới tự theo kênh thử nghiệm. Ghi chú phát hành của bản này còn mang tiêu đề "Chưa phát hành" (thẻ được đẩy trước khi đổi tiêu đề).

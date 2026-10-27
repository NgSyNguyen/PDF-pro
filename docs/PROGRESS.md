# PDF_Pro — Nhật ký tiến độ & bàn giao

> Tệp này được `CLAUDE.md` nạp tự động: mọi phiên Claude Code (trên máy hay trên cloud) đọc nó ngay khi mở repo.
> **Cập nhật tệp này ở cuối mỗi lát** (trạng thái, commit, quyết định, việc tiếp theo) trong cùng commit với lát đó.
> Nguồn sự thật về thiết kế vẫn là `docs/SPEC.md`; lộ trình là `docs/PROMPTS.md`; luật là `AGENTS.md`.

Cập nhật lần cuối: 27/10/2026 — Phase 5, sau lát 9.

## 1. Chỉ đạo hiện hành của chủ dự án

- **27/10/2026:** agent **tự quyết** mọi việc theo phương án tốt nhất và **làm tiếp các phần sau mà không cần hỏi lại**. Bước "trình bày kế hoạch, chờ Duyệt" trong `AGENTS.md` được miễn; mọi luật khác của `AGENTS.md` (kiến trúc, license, test golden-file, chuỗi trong resource, Conventional Commits, mẫu báo cáo cuối nhiệm vụ) vẫn giữ nguyên.
- Mỗi lát: code → build Release 0 cảnh báo → test → cập nhật tệp này → commit → push → kiểm CI trên PR.
- Trả lời chủ dự án bằng tiếng Việt; code, tên, comment bằng tiếng Anh.

## 2. Git, PR và CI

| Mục | Giá trị |
|---|---|
| Nhánh làm việc | `claude/laughing-pasteur-x9yt44` |
| PR | NgSyNguyen/PDF-pro#1 (draft → `main`) — gom toàn bộ phần còn lại của Phase 5 |
| CI | `.github/workflows/build.yml`, `windows-latest`; chạy khi push vào `main` hoặc trên pull request (nhánh không có PR thì **không** chạy CI) |
| CI gần nhất | `292e6a3`: 1.740 test, 0 lỗi, 1.726 đạt, 14 bỏ qua (UI test FlaUI tự bỏ qua khi không đặt `PDFPRO_UI_TESTS=1`) |

- Trên máy chủ dự án có `README.md` chưa track (không rõ nguồn) — **chưa commit**; README chính thức để tới P13.
- Máy chủ dự án cần `git config user.name/user.email` trong repo (đã từng thiếu → commit thất bại im lặng).
- Repo lưu LF (`.gitattributes`: `* text=auto`), checkout Windows ra CRLF — bình thường.

## 3. Build & test

**Windows (máy phát triển, CI):**
```powershell
dotnet build PdfPro.slnx -c Release
dotnet test --solution PdfPro.slnx -c Release
$env:PDFPRO_UI_TESTS=1; dotnet test --project tests/PdfPro.App.UiTests -c Release   # chạy tay, cần desktop
```

**Linux (container cloud của Claude Code):**
- `dot.net`/`builds.dotnet.microsoft.com` bị proxy chặn → cài SDK từ kho Ubuntu: `apt-get install -y dotnet-sdk-10.0` (10.0.1xx khớp `global.json`).
- Build cả WPF được: `dotnet build PdfPro.slnx -c Release -p:EnableWindowsTargeting=true`.
- Chạy được: `Core.Tests`, `Operations.Tests`, `Infrastructure.Tests` (`dotnet test --project tests/PdfPro.X.Tests -c Release --no-build`).
- **Không** chạy được: `App.Tests`, `Pdfium.Tests` (net10.0-windows) → dựa vào CI của PR.
- Lỗi chỉ có trên Linux (bỏ qua, trên Windows đều xanh): Core 1 test (`SessionSource_RejectsUnsafeIdsAndPaths("C:\\s0.pdf")`), Infrastructure 27 test (đường dẫn `C:\`, khóa tệp, `.exe` giả, named pipe).

## 4. Trạng thái theo phase

| Phase | Trạng thái |
|---|---|
| P1 Nền móng | Xong (`7b968e2`) |
| P2 PDFium + Viewer | Xong (`6c0456e` … `e4541a1`) |
| P3 Phiên tài liệu & tổ chức trang | Xong (`4ff9664` … `c1bbc4c`), có nghiệm thu FlaUI |
| P4 Gộp, tách & bố cục | Xong (`b541fcd` … `1b0a5c2`), có nghiệm thu FlaUI và mục SPEC |
| **P5 Chèn & đóng dấu** | **Đang làm** — xem bảng lát dưới |
| P6–P13 | Chưa bắt đầu (`PdfPro.Ocr` trống, `PdfPro.Cli` mới có `--version`) |

### Phase 5 — các lát

| Lát | Nội dung | Commit | Trạng thái |
|---|---|---|---|
| 1 | Core: đối tượng overlay (JSON), `RotatedBox`, snapping, `TextLayout`, mẫu stamp `{page}…`, Bates, đánh số La Mã | `1dc6110` | Xong |
| 2 | `FontLibrary`: Noto Sans/Serif, Be Vietnam Pro đóng gói + font cài trên máy viết đủ tiếng Việt | `464d0de` | Xong |
| 3 | `edit.overlay`: chữ, ảnh, hình, đường lên trang (form XObject, `PageCoordinateMapper`) | `9255aa2` | Xong |
| 4 | Operations `stamp.watermark/background/headerFooter/pageNumbers/bates/remove` | `3e6911c` | Xong |
| 5 | Trình chỉnh sửa trang: đối tượng trên viewer, 8 nút kéo, xoay, snap, Áp dụng = 1 bước hoàn tác | `0f9046c` | Xong |
| 6 | Gõ chữ tại chỗ, panel thuộc tính, chèn ảnh, áp dụng trước khi lưu/đóng | `f145f5d` | Xong |
| 7 | Chữ ký tay (vẽ / gõ / ảnh), thư viện mã hóa DPAPI | `11cf4ed` | Xong |
| 8 | Hộp thoại đóng dấu có xem trước (Hình mờ, Nền, Đầu & chân trang, Số trang, Gỡ dấu), nút ribbon | `8846ae0`, `292e6a3` | Xong, CI xanh |
| 9 | Hộp thoại Bates nhiều tệp (`stamp.bates`): danh sách tệp, số chạy nối tiếp, nhãn, thư mục ra, chạy nền | commit `feat(app): Bates numbering…` | Xong, chờ CI |
| 10 | Mục Phase 5 trong SPEC + `Phase5AcceptanceTests` (FlaUI) | — | Tiếp theo |

Sau lát 10: sang **Phase 6** theo `docs/PROMPTS.md` (chọn chữ, tìm kiếm có/không dấu, annotation chuẩn PDF, bình luận).

## 5. Bản đồ mã Phase 5

- **Core:** `Overlay/OverlayItems.cs` (đối tượng, `OverlayLimits`), `Overlay/OverlaySnapping.cs`, `Geometry/RotatedBox.cs`, `Geometry/PdfMatrix.cs`, `Text/TextLayout.cs`, `Text/FontFamilies.cs`, `Stamping/StampTemplate.cs` (`Parse`, `Render`, `Escape`), `Stamping/PageNumbering.cs`, `Stamping/BatesNumbering.cs`, `Stamping/StampPlacement.cs`, `Imaging/SignatureImages.cs`, `Imaging/ExifOrientation.cs`, `Organizing/MaterializedGeometry.cs`.
- **Operations/Stamping:** `DocumentContentOperation` (mở / kiểm / lưu chung cho thao tác nội dung), `StampForms` (vẽ bằng PDFsharp ra form XObject), `PageStamper` (đặt form qua ma trận của mapper, `q/Q`, `/Resources` riêng từng trang, OCG có marker), `OverlayPainter`, `TextZones`, `StampContent` (marker, layer "Watermark"/"Background"…), từng operation + `StampParams.cs`. `RemoveStampsOperation.Scan(path)` đếm trang mang từng loại dấu.
- **Operations/Fonts:** `FontLibrary` (resolver của PDFsharp; nghiêng giả được, đậm không bao giờ giả), `TrueTypeFont`.
- **App:**
  - Trình chỉnh sửa: `ViewModels/Editing/PageEditorViewModel.cs`, `OverlayObjectViewModel.cs`, `OverlayPropertiesViewModel.cs`, `Controls/OverlayLayer.cs`, `Services/Editing/OverlayDrawing.cs`, `ScreenFonts.cs`, `ImageImporter.cs`.
  - Chữ ký tay: `SignatureViewModel.cs`, `Services/Editing/SignatureLibrary.cs` (DPAPI, 20 mục), `SignatureRenderer.cs`, `Views/SignatureWindow.xaml`.
  - Đóng dấu: `ViewModels/Editing/StampDialogViewModel.cs` (lớp chung: trang áp dụng, xem trước sau 250 ms), `StampDialogs.cs` (5 hộp thoại), `Views/StampWindow.xaml`, `Services/Editing/StampPreview.cs` (`StampPreview` + `DocumentStamps.ScanAsync`).
  - Bates nhiều tệp: `Services/Documents/BatesService.cs` (đọc số trang bằng PDFium, từ chối tệp có mật khẩu), `ViewModels/Editing/BatesViewModel.cs`, `Views/BatesWindow.xaml`, `ViewModels/MainWindowViewModel.Bates.cs` (lệnh `BatesCommand`, chạy nền ở thanh trạng thái, mở thư mục khi xong).
  - Nối dây: `DocumentTabViewModel.cs` (`StampServices`, lệnh `Stamp*Command`, `RemoveStampsCommand`; `DocumentTabFactory` nhận `IDocumentSaver`), `MainWindowViewModel.cs` (ribbon nhóm "Đóng dấu"), `Services/Documents/ContentChanger.cs` (thao tác nội dung → revision mới → 1 bước hoàn tác), `HistoryDescriptions.cs`.
- **Test:** `tests/PdfPro.Operations.Tests/Stamping/*`, `Fonts/*`; `tests/PdfPro.App.Tests/ViewModels/PageEditorViewModelTests.cs`, `StampDialogViewModelTests.cs`, `Services/SignatureTests.cs`; `tests/PdfPro.Core.Tests/Stamping|Overlay|Geometry/*`.

## 6. Quyết định & bài học (đọc trước khi sửa phần liên quan)

- **Xem trước đóng dấu = chạy operation thật** trên bản xuất 1 trang của trang hiện tại (ghi một lần vào `work\`), PDFium render; không mô phỏng. Chữ phụ thuộc trang được viết sẵn (`TextsOn`/`TextOn`) rồi `StampTemplate.Escape` để tệp 1 trang ra đúng chữ của trang đó.
- **`{filename}` trong phiên phải lấy từ tham số** (`HeaderFooterParams.FileName`, `PageNumbersParams.FileName`): tệp đầu vào của thao tác nội dung trong phiên là tệp tạm tên GUID (`ContentChanger`), không phải tên tài liệu.
- **Gỡ dấu quét "tài liệu như đang hiển thị"**: xuất toàn bộ plan ra `work\stamps-*.pdf`, `Scan` ở luồng nền, xóa bản tạm. Tài liệu 1.000 trang có thể mất 1–2 s, chưa có thanh tiến độ (rủi ro đã biết).
- **`DocumentSaveException` kế thừa thẳng `Exception`** — mọi bộ lọc `catch (… when ex is …)` quanh `IDocumentSaver` phải liệt kê nó.
- **Tránh đặt tên thuộc tính trùng tên lớp tĩnh** đang dùng trong cùng lớp (lỗi CS0236 với `FontFamilies.Default`; thuộc tính đã đổi thành `FontNames`).
- **Bates của tài liệu đang sửa:** xuất bản sao vào `work\bates-{guid}\{tên tài liệu}` — bản đánh số lấy tên theo tệp vào, nên bản sao phải mang đúng tên tài liệu; thư mục tạm bị xóa sau khi chạy. Tệp có mật khẩu bị từ chối cho tới P8 (PDFsharp mở không mật khẩu). Thư mục ra mặc định `…\Đã đánh số Bates` cạnh tệp đầu; "Ghi đè" + thư mục chứa tệp gốc bị chặn.
- `MainWindowViewModel` nhận `IBatesService?` là tham số tùy chọn cuối (test: `Create(isDebugBuild, withBates)`).
- `DocumentTabFactory` có tham số tùy chọn ở cuối (`screenFonts`, `screenImages`, `signatures`, `saver`): test dựng factory không truyền chúng thì tính năng tương ứng tắt — test của tính năng phải truyền vào (ví dụ `saver: _saver`).
- Test ribbon `Ribbon_OnlyImplementedCommandsAreAvailable` liệt kê đủ id lệnh đã có — thêm nút mới phải thêm id vào đó.
- PDFsharp **ghim 6.2.4** (6.5+ là AGPL, có test canh). Không thêm package mới nếu stack hiện có làm được.

## 7. Rủi ro / nợ đang mở

- Gỡ dấu trên tài liệu lớn: không có tiến độ khi quét.
- `PdfPro.Benchmarks`, `PdfPro.Worker` chưa tạo (để P13).

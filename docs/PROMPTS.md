# PDF_Pro — Prompt theo phase

> Paste lần lượt từng phase vào AI agent, mỗi phase một phiên chat mới. Luật chung nằm trong `AGENTS.md`, đặc tả trong `docs/SPEC.md`.

## Lộ trình & prompt theo phase

| Phase | Tên | Kết quả kiểm chứng được | Độ khó |
|---|---|---|---|
| P1 | Nền móng | Solution chuẩn, cửa sổ Fluent, sáng/tối, vi/en, log, CI | ★ |
| P2 | Engine PDFium + Viewer | Mở & xem mượt file 1.000 trang, zoom, thumbnail, bookmark, in | ★★★ |
| P3 | Phiên tài liệu & tổ chức trang | PagePlan, undo/redo, lưu nguyên tử, khôi phục crash | ★★★★ |
| P4 | Gộp, tách & bố cục | Gộp giữ bookmark, 6 chế độ tách, lẻ/chẵn, N-up, booklet | ★★ |
| P5 | Chèn & đóng dấu | Chữ tiếng Việt, ảnh, chữ ký tay, watermark, số trang, Bates | ★★★ |
| P6 | Chú thích & tìm kiếm | Chọn chữ, tìm không dấu, annotation chuẩn PDF, bình luận | ★★★ |
| P7 | Sửa nội dung & biểu mẫu | Sửa text gốc, thiết kế/điền form, trộn dữ liệu | ★★★★★ |
| P8 | Bảo mật & ký số | AES-256, redaction thật có hậu kiểm, ký số, xác minh | ★★★★ |
| P9 | Tối ưu & chuyển đổi | Nén, sửa file hỏng, ảnh ↔ PDF, Office → PDF, PDF/A | ★★★ |
| P10 | OCR & công cụ scan | OCR tiếng Việt giữ ảnh gốc, deskew, xóa trang trắng | ★★★ |
| P11 | So sánh & trích xuất | Diff văn bản + hình ảnh, bảng → Excel, bookmark, metadata | ★★★ |
| P12 | Tự động hóa | Ctrl+K, workflow, chạy lô, hot folder, CLI | ★★★ |
| P13 | Hoàn thiện & phát hành | Worker cô lập, torture test, benchmark, installer, auto-update | ★★★ |

Mỗi phase gồm: mục tiêu ngắn, **khung prompt nền tối** (bôi đen và copy toàn bộ khung vào agent), và khung **Kiểm tra trước khi sang phase sau** để bạn tự nghiệm thu bằng tay.

> [!TIP]
> **Mẹo giữ chất lượng qua 13 phase**
> - Trước khi paste prompt mới, chạy `git status` để chắc chắn phase trước đã commit sạch.
> - Nếu kế hoạch agent đưa ra lệch SPEC, trả lời "Không duyệt — bám SPEC mục …" thay vì để nó code rồi sửa.
> - Phase dài (P3, P7, P8) có thể tách làm 2 phiên: phiên 1 làm phần A, phiên 2 làm phần B — paste lại prompt và ghi "Phiên này chỉ làm phần B, phần A đã xong".

### P1 — Nền móng: solution, khung ứng dụng, CI

Dựng bộ khung mà 12 phase sau đứng lên: cấu trúc project, DI, log, theme, đa ngôn ngữ, Undo/Redo stack và CI. Chưa xử lý PDF ở phase này.

```text
Đọc AGENTS.md và docs/SPEC.md. Đây là Phase 1/13: dựng nền móng. Chưa xử lý PDF ở phase này.

## Nhiệm vụ
1. Tạo solution PdfPro.slnx đúng cấu trúc trong SPEC (mục "Cấu trúc solution"): Core, Pdfium, Operations, Ocr, Infrastructure, App, Cli và 3 project test (Core.Tests, Operations.Tests, App.Tests). Worker và Benchmarks để sau.
2. Directory.Build.props: net10.0 (App dùng net10.0-windows + UseWPF), Nullable enable, TreatWarningsAsErrors, LangVersion latest, Deterministic. Directory.Packages.props quản lý version tập trung. Thêm .editorconfig (quy ước C# chuẩn), .gitignore cho .NET, global.json ghim SDK 10.
3. PdfPro.App:
   - Generic Host (Microsoft.Extensions.Hosting) khởi động trong App.xaml.cs; DI cho ViewModel và service; Serilog ghi file rolling tại %LOCALAPPDATA%\PdfPro\logs + debug output.
   - Bắt mọi exception chưa xử lý (DispatcherUnhandledException, TaskScheduler.UnobservedTaskException, AppDomain.UnhandledException) → ghi log + hộp thoại thân thiện tiếng Việt; không crash im lặng.
   - Cửa sổ chính dùng WPF-UI FluentWindow (Mica nếu hệ điều hành hỗ trợ); theme Sáng/Tối/Theo hệ thống, lưu lựa chọn.
   - Bố cục khung: thanh công cụ dạng ribbon gọn với các nhóm Tệp, Trang, Chỉnh sửa, Chú thích, Bảo mật, Chuyển đổi, Công cụ (nút chưa có chức năng thì disabled); vùng tab tài liệu (đa tài liệu); sidebar trái (thumbnail/bookmark — placeholder); khung xem giữa (placeholder); panel phải (thuộc tính — placeholder); thanh trạng thái dưới (số trang, zoom, tiến độ tác vụ).
   - Màn hình chào khi chưa mở file: nút Mở file, vùng kéo-thả lớn, danh sách file gần đây (placeholder).
   - Đa ngôn ngữ: Strings.resx (vi, mặc định) + Strings.en.resx; binding qua markup extension hoặc service; đổi ngôn ngữ trong Cài đặt (áp dụng sau khi khởi động lại là chấp nhận được).
   - SettingsService: lưu JSON tại %LOCALAPPDATA%\PdfPro\settings.json, có số phiên bản schema; file lỗi thì dùng mặc định và ghi log.
4. PdfPro.Core: tạo các kiểu nền tảng IPdfOperation, PdfOperation<TParams>, OperationKind, OperationContext, OperationResult, OperationProgress, IUndoableCommand. Cài đặt ĐẦY ĐỦ UndoRedoStack + unit test: Do/Undo/Redo, giới hạn độ sâu, xóa nhánh redo khi có lệnh mới, CompositeCommand gộp nhiều lệnh thành 1 bước, sự kiện CanUndo/CanRedo thay đổi.
5. PdfPro.Infrastructure: IExternalToolRunner (Process + ArgumentList, không shell, timeout, CancellationToken, thu stdout/stderr, kill cả cây tiến trình khi hủy) + test bằng lệnh "dotnet --version". Lớp AppPaths cho các thư mục sessions, logs, cache, tools.
6. PdfPro.Cli: lệnh "pdfpro --version" bằng System.CommandLine.
7. CI: .github/workflows/build.yml chạy trên windows-latest: restore, build Release, test, upload artifact bản build của App.
8. Script tools/fetch-tools.ps1 dạng khung (in hướng dẫn) — sẽ tải qpdf, tesseract ở các phase sau.
9. Tạo THIRD_PARTY_NOTICES.md liệt kê các package đã dùng.

## Nghiệm thu
- dotnet build -c Release: 0 warning; dotnet test: xanh.
- Chạy App: cửa sổ Fluent đúng bố cục; đổi Sáng/Tối tức thì; đổi ngôn ngữ vi/en hoạt động.
- Trong bản Debug có nút "Ném lỗi thử" → hiện hộp thoại thân thiện + có dòng log.

Làm theo quy trình chuẩn trong AGENTS.md: trình bày kế hoạch và chờ tôi duyệt trước khi tạo file.
```

> [!TIP]
> **Kiểm tra trước khi sang P2**
> - Mở thư mục log, thấy file log của ngày hôm nay.
> - Xóa file `settings.json` rồi mở app — app vẫn chạy với cài đặt mặc định.
> - Push lên GitHub, tab Actions báo build xanh.

### P2 — Engine PDFium & trình xem tài liệu

Mở và xem PDF mượt như các trình đọc thương mại, kể cả file nghìn trang. Đây cũng là nơi dựng hai nền tảng quan trọng nhất về kỹ thuật: `PdfiumGate` (an toàn đa luồng) và `PageCoordinateMapper` (hệ tọa độ).

```text
Đọc AGENTS.md và docs/SPEC.md (mục "Luồng xử lý & đa luồng", "Hệ tọa độ"). Phase 2/13: mở và xem PDF mượt.

## A. PdfPro.Pdfium — wrapper an toàn
1. Tham chiếu PDFiumCore. Kiểm tra API thật trong package (các nhóm hàm fpdfview, fpdf_text, fpdf_doc, fpdf_edit…) — không đoán tên hàm.
2. PdfiumGate: một luồng chuyên dụng thực thi MỌI lời gọi PDFium (hàng đợi + TaskCompletionSource). Khởi tạo thư viện đúng một lần khi app start, hủy khi thoát. API: Task<T> RunAsync<T>(Func<T> work, CancellationToken ct).
3. SafeHandle cho document, page, text page, bitmap. Lớp PdfiumDocument: Open(path, password?), PageCount, GetPageSize(index) (dùng hàm lấy kích thước không cần load trang), GetPageRotation, metadata, cây bookmark (tiêu đề + trang đích), Dispose.
4. Phân loại lỗi: đọc mã lỗi của PDFium → exception có kiểu riêng (PasswordRequired, WrongPassword, CorruptedFile, UnsupportedSecurity, FileNotFound) để UI xử lý đúng.
5. RenderPageAsync(pageIndex, scale, flags: annotations / LCD text / grayscale / printing) → BitmapSource BGRA đã Freeze(). Nền trắng; tôn trọng /Rotate và CropBox.
6. Test trên samples/: file thường, có mật khẩu, hỏng, 1.000 trang; render trang 1 so với ảnh tham chiếu (cho phép sai số pixel nhỏ); không rò rỉ handle (đếm số handle mở/đóng).

## B. Viewer trong PdfPro.App
1. PageCoordinateMapper (Core) + unit test: 4 góc xoay × CropBox lệch gốc × 3 mức zoom; round-trip ScreenToPdf(PdfToScreen(p)) == p với sai số dưới 0.01.
2. DocumentViewer: cuộn liên tục dọc (VirtualizingStackPanel, VirtualizationMode=Recycling, ScrollUnit=Pixel), khoảng cách trang, bóng đổ nhẹ; chỉ render trang đang thấy + 1 trang đệm mỗi phía; hủy render của trang đã cuộn qua.
3. Zoom: Ctrl + cuộn chuột (giữ cố định điểm dưới con trỏ), Ctrl +/−, Vừa chiều rộng, Vừa trang, 100%; chế độ 1 trang / 2 trang đối diện. Render 2 nấc: dùng ảnh cache phóng tạm khi đang zoom, render nét sau 150 ms debounce.
4. RenderCache LRU theo byte (mặc định 512 MB, chỉnh trong Cài đặt), key = (revisionId, pageIndex, scaleBucket, rotation).
5. Sidebar Thumbnail: ảnh rộng 160 px render ưu tiên thấp, cache đĩa tại %LOCALAPPDATA%\PdfPro\cache; click → nhảy trang; trang hiện tại có viền nổi bật.
6. Sidebar Bookmark: cây bookmark, click → nhảy đến đích.
7. Điều hướng: ô số trang (gõ số + Enter), Home/End/PageUp/PageDown, Ctrl+G.
8. Mở file: hộp thoại; kéo-thả 1 hoặc nhiều file (mỗi file 1 tab); 10 file gần đây (tự loại file không còn tồn tại); mở bằng dòng lệnh "PdfPro.exe a.pdf"; single-instance — lần mở thứ hai chuyển đường dẫn sang cửa sổ đang chạy qua named pipe.
9. File có mật khẩu: hộp thoại nhập, sai thì báo và cho nhập lại; không lưu mật khẩu.
10. Thuộc tính tài liệu (Ctrl+D): tiêu đề, tác giả, phiên bản PDF, số trang, khổ giấy, có mã hóa / chữ ký số / form / JavaScript hay không.
11. In: hộp thoại in → render từng trang theo DPI máy in, chọn khoảng trang, co vừa giấy; chạy nền có progress, hủy được.

## Nghiệm thu
- File 1.000 trang: trang đầu hiện dưới 2 giây; cuộn không giật; RAM ổn định khi cuộn từ đầu đến cuối (không tăng mãi).
- Ctrl + cuộn chuột giữ đúng điểm dưới con trỏ.
- File hỏng / sai mật khẩu → thông báo rõ ràng, app không crash.

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P3**
> - Mở cùng lúc 5 file lớn, chuyển tab qua lại: không đơ, RAM không vượt ngân sách cache quá nhiều.
> - Mở file có trang xoay 90° (`rotated-cropbox.pdf`): hiển thị đúng chiều.
> - Kéo-thả file từ Explorer và mở bằng chuột phải → "Open with" đều hoạt động.

### P3 — Phiên tài liệu, PagePlan, Undo/Redo & tổ chức trang

Trái tim của ứng dụng: mọi chỉnh sửa về sau đều dựa trên phase này. Agent phải trình bày thiết kế lớp và chờ duyệt.

```text
Đọc AGENTS.md và docs/SPEC.md (mục "Ba khái niệm lõi"). Phase 3/13 — trái tim của app. Trước khi code: trình bày thiết kế lớp (sơ đồ dạng text: lớp, quan hệ, luồng dữ liệu khi người dùng xoay trang rồi bấm Lưu) và chờ tôi duyệt.

## A. Core & Operations
1. DocumentSession: Id, thư mục phiên %LOCALAPPDATA%\PdfPro\sessions\{id}\, danh sách Source (bản sao file gốc + các file được chèn vào), CurrentRevision, PagePlan, UndoRedoStack, IsDirty, OriginalPath.
2. PagePlan bất biến: IReadOnlyList<PageRef>; PageRef = record(SourceId, SourcePageIndex, RotationDelta 0/90/180/270, CropBoxOverride?, PageKey Guid). Hàm thuần trả về PagePlan mới: Move(indices, targetIndex), Rotate(indices, delta), Delete(indices), Duplicate(indices), InsertBlank(index, size), InsertFromSource(index, sourceId, pages), Crop(indices, rect). Unit test đầy đủ, kể cả chỉ số biên.
3. Mỗi thao tác trên là một IUndoableCommand lưu plan trước/sau → undo tức thì. Thao tác nội dung (các phase sau) dùng RevisionCommand: undo = quay về revision trước + plan tương ứng.
4. Materializer (PdfPro.Operations, PDFsharp): PagePlan → file PDF mới: import trang theo thứ tự từ các source, áp góc xoay, CropBox; giữ bookmark nếu trang đích còn tồn tại (cập nhật trỏ đúng vị trí mới); giữ annotation và link nội bộ khi có thể. Test golden-file: sau Move/Rotate/Delete kiểm tra số trang, thứ tự (so text từng trang), góc xoay.
5. manifest.json của phiên ghi sau mỗi thay đổi (source, revision, plan, lịch sử lệnh). Khi app khởi động còn phiên chưa đóng (do crash) → hỏi "Khôi phục tài liệu chưa lưu?" và khôi phục đúng trạng thái.
6. Lưu (Ctrl+S) ghi đè an toàn: file tạm cùng thư mục → flush → File.Replace kèm backup tạm; file đang bị app khác khóa → thông báo rõ và đề xuất Lưu thành (Ctrl+Shift+S). File có chữ ký số → cảnh báo lưu sẽ làm chữ ký mất hiệu lực, đề xuất Lưu thành.
7. Đóng tab có thay đổi chưa lưu → hỏi Lưu / Không lưu / Hủy. Thoát app → hỏi lần lượt từng tab.

## B. UI — chế độ "Sắp xếp trang"
1. Lưới thumbnail lớn (thanh trượt chỉnh kích thước), ảo hóa cho tài liệu nghìn trang.
2. Chọn: click, Ctrl+click, Shift+click, kéo vùng chọn (rubber band), Ctrl+A, ô gõ khoảng trang "1-5, 8, 10-".
3. Kéo-thả sắp xếp nhiều trang cùng lúc (gong-wpf-dragdrop) có vạch chỉ vị trí chèn; kéo file PDF từ Explorer vào lưới → chèn tại vị trí thả.
4. Thanh công cụ + menu chuột phải + phím tắt: Xoay trái/phải, Xóa (Delete), Nhân bản, Chèn trang trắng (khổ: giống trang kề / A4 / A3 / Letter), Chèn từ file…, Trích trang ra file mới, Cắt lề (hộp thoại crop có xem trước, áp cho trang chọn / tất cả / lẻ / chẵn), Đổi khổ giấy (co giãn nội dung vừa khổ mới).
5. Undo/Redo: Ctrl+Z / Ctrl+Y; menu Lịch sử hiển thị tên từng lệnh; tiêu đề tab có dấu * khi chưa lưu.
6. Mọi thao tác trên plan phản hồi dưới 50 ms với file 1.000 trang (chỉ render lại thumbnail trang bị xoay).

## Nghiệm thu
- Xoay/xóa/sắp xếp 200 trang của file 1.000 trang: tức thì; Undo về đúng trạng thái; Lưu rồi mở bằng trình duyệt thấy đúng.
- Kill tiến trình app bằng Task Manager giữa chừng → mở lại được đề nghị khôi phục và khôi phục đúng.
- File gốc không thay đổi byte nào trước khi bấm Lưu (kiểm bằng SHA-256).

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P4**
> - Làm 30 thao tác liên tiếp rồi Undo hết: tài liệu về đúng ban đầu.
> - Lưu file đang mở trong một trình đọc PDF khác: app báo lỗi rõ ràng, không mất dữ liệu.
> - Rút điện giả lập: kill app ngay sau khi bấm Lưu — file gốc vẫn mở được (hoặc đã là bản mới hoàn chỉnh).

### P4 — Gộp, tách & bố cục trang nâng cao

Hoàn tất bộ "cắt – ghép" chuyên nghiệp. Sau phase này bạn đã có MVP dùng hằng ngày.

```text
Đọc AGENTS.md và docs/SPEC.md. Phase 4/13. Mọi tính năng dưới đây là IPdfOperation (chạy nền, progress, hủy được) và có hộp thoại riêng với XEM TRƯỚC kết quả (số file, số trang mỗi file, tên file dự kiến) trước khi chạy.

## Gộp (organize.merge)
- Danh sách file (kéo-thả, sắp xếp, xóa), mỗi file chọn khoảng trang.
- Tùy chọn: giữ bookmark gốc (lồng dưới bookmark mang tên file); tạo bookmark theo tên file; thêm trang mục lục tự động có link; chuẩn hóa khổ giấy (không đổi / theo trang đầu / A4); chèn trang trắng để mỗi file bắt đầu ở mặt trước khi in 2 mặt.
- File có mật khẩu trong danh sách → hỏi mật khẩu ngay khi thêm.

## Tách (organize.split) — 6 chế độ
1. Theo khoảng: "1-3, 4-10, 11-" → mỗi khoảng một file.
2. Mỗi N trang.
3. Theo bookmark cấp k (tên file = tiêu đề bookmark đã loại ký tự cấm trong tên file Windows).
4. Theo dung lượng tối đa (MB): ước lượng theo trang rồi kiểm tra dung lượng thật sau khi ghi.
5. Theo trang trắng phân cách: BlankPageDetector (tỉ lệ pixel tối, bỏ qua 5% lề, ngưỡng chỉnh được); trang phân cách bị loại khỏi kết quả.
6. Theo văn bản trên trang (regex, ví dụ "Số:\s*\d+/QĐ"): mỗi lần khớp bắt đầu file mới.
- Mẫu đặt tên: {name}_{index:000}, {bookmark}, {first}-{last}; chọn thư mục đích; mở thư mục khi xong.

## Bố cục
- organize.interleave: trộn file mặt trước + file mặt sau (tùy chọn mặt sau bị đảo ngược thứ tự) — cho trường hợp scan 2 mặt bằng máy 1 mặt.
- layout.nup: 2/4/6/8/9/16 trang trên một tờ, thứ tự Z hoặc N, lề, viền.
- layout.booklet: sắp trang để in gập đôi thành sổ, tự thêm trang trắng cho đủ bội số 4.
- layout.tile: chia 1 trang khổ lớn thành nhiều tờ A4, có vùng chồng mép để dán.

## Test
Golden-file cho từng chế độ tách (số file, số trang, text trang đầu mỗi file); merge giữ bookmark; interleave với mặt sau đảo ngược; booklet 10 trang → 12 trang đúng thứ tự in.

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P5**
> - Gộp 20 file, mở kết quả: bookmark đầy đủ, mục lục click được.
> - Tách theo bookmark một tài liệu có tiêu đề tiếng Việt: tên file đúng dấu, không lỗi ký tự.
> - In thử booklet: gập đôi đọc đúng thứ tự trang.

### P5 — Chèn & đóng dấu: chữ, ảnh, chữ ký tay, watermark, số trang

Lớp overlay tương tác cho phép thêm nội dung mới lên trang, cùng bộ đóng dấu hàng loạt. Điểm khó: tiếng Việt và hệ tọa độ.

```text
Đọc AGENTS.md và docs/SPEC.md (mục "Hệ tọa độ", "Tiếng Việt"). Phase 5/13.

## A. Lớp overlay tương tác (App)
1. Mỗi trang trong viewer có OverlayCanvas phía trên ảnh render. Đối tượng tạm (chưa ghi vào PDF): TextBox, Image, Rectangle, Ellipse, Line, Arrow, Whiteout (chữ nhật trắng không viền), SignatureImage.
2. Tương tác: chọn, kéo, resize bằng 8 nút, xoay (giữ Shift để bước 15°), snap vào mép trang / đối tượng khác kèm đường gióng; phím mũi tên dịch 1 pt (Shift = 10 pt); Delete xóa; Ctrl+C / Ctrl+V được cả giữa các trang.
3. Panel thuộc tính bên phải: font (font đóng gói + font hệ thống có hỗ trợ tiếng Việt), cỡ, màu, đậm/nghiêng, căn lề, giãn dòng, độ mờ, viền, nền.
4. Vị trí lưu bằng đơn vị PDF qua PageCoordinateMapper — zoom hoặc trang có /Rotate không làm lệch đối tượng.
5. Nút "Áp dụng" (hoặc tự áp dụng khi rời chế độ chỉnh sửa) → operation edit.overlay nhận danh sách đối tượng (serialize JSON được) → vẽ vào trang bằng PDFsharp (XGraphics với tùy chọn Append) → tạo Revision mới, đúng 1 bước undo.
6. Tiếng Việt: IFontResolver của PDFsharp trỏ tới font trong Assets/Fonts (Noto Sans, Noto Serif, Be Vietnam Pro — license OFL, thêm vào THIRD_PARTY_NOTICES); nhúng subset. Test: chèn "Cộng hòa xã hội chủ nghĩa Việt Nam — Độc lập - Tự do - Hạnh phúc" rồi trích lại bằng PdfPig phải ra đúng chuỗi (sau NFC).
7. Ảnh: PNG giữ alpha; JPEG giữ nguyên dữ liệu gốc (không nén lại); xoay theo EXIF.

## B. Chữ ký tay (dạng ảnh)
Hộp thoại tạo chữ ký: vẽ bằng chuột/bút (InkCanvas, làm mượt nét), gõ tên với font viết tay, hoặc nhập ảnh (tự tách nền trắng thành trong suốt). Thư viện chữ ký lưu cục bộ, mã hóa bằng DPAPI (ProtectedData, phạm vi CurrentUser). Trong UI ghi rõ: đây là chữ ký hình ảnh, KHÁC chữ ký số (Phase 8).

## C. Đóng dấu hàng loạt — mỗi mục là một operation có hộp thoại + xem trước trực tiếp trên trang hiện tại
1. stamp.watermark: chữ hoặc ảnh; font, cỡ, màu, độ mờ, góc xoay; vị trí 9 điểm hoặc lát kín trang; nằm trên/dưới nội dung; áp cho khoảng trang / lẻ / chẵn; ghi vào Optional Content Group (layer "Watermark") để bật/tắt hoặc gỡ về sau.
2. stamp.headerFooter: 6 vùng (trái/giữa/phải × trên/dưới); biến {page}, {pages}, {date:dd/MM/yyyy}, {filename}, {title}; lề; bỏ qua trang đầu; số bắt đầu.
3. stamp.pageNumbers: kiểu 1,2,3 / i,ii,iii / "Trang 1/10"; vị trí; bắt đầu từ trang k với số n.
4. stamp.bates: tiền tố, hậu tố, số chữ số (000123), số bắt đầu; áp cho NHIỀU file với số chạy nối tiếp giữa các file.
5. stamp.background: màu hoặc ảnh nền, nằm dưới nội dung.
6. stamp.remove: gỡ watermark / header-footer do chính PDF_Pro tạo (nhận diện qua OCG hoặc marker trong content stream).

## Nghiệm thu
- Đối tượng không lệch khi zoom 50%–400% và trên trang /Rotate 90.
- Tiếng Việt hiển thị đúng khi mở bằng Adobe Acrobat Reader và Chrome; chữ chèn vào tìm kiếm và copy được.
- Undo 1 bước gỡ toàn bộ một lần "Áp dụng".

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P6**
> - Chèn chữ có đủ dấu (ẳ, ỡ, ự, Ỷ…) bằng cả 3 font đóng gói, mở trên máy khác không cài font đó — vẫn đúng.
> - Đánh số Bates cho 3 file liên tiếp: số chạy nối tiếp, không trùng.
> - Gỡ watermark vừa thêm: trang về như cũ.

### P6 — Chọn chữ, tìm kiếm & chú thích (annotation)

Annotation phải ghi đúng chuẩn PDF để Acrobat và trình duyệt đọc, sửa được. Tìm kiếm phải hiểu tiếng Việt.

```text
Đọc AGENTS.md và docs/SPEC.md. Phase 6/13.

## A. Lớp chữ & tìm kiếm
1. TextLayerService (API text của PDFium): ký tự + hộp bao theo trang, cache theo revision. Chọn chữ bằng chuột (kéo; double-click chọn từ; triple-click chọn dòng), chọn xuyên trang, Ctrl+C copy (NFC, nối dòng hợp lý, bỏ gạch nối cuối dòng khi phù hợp).
2. Tìm kiếm Ctrl+F: phân biệt hoa/thường, toàn từ, bỏ dấu tiếng Việt ("hoa" khớp "hòa", "hóa"), regex. Chạy nền trên toàn tài liệu, kết quả đổ dần vào panel kèm ngữ cảnh; F3 / Shift+F3 tới/lui; tô sáng mọi kết quả trên trang; hủy được.
3. Trang không có lớp chữ (scan) → hiện gợi ý "Trang này là ảnh — chạy OCR?" (nối với Phase 10).

## B. Annotation — ghi bằng API annotation của PDFium, đúng chuẩn PDF
1. Markup theo vùng chọn chữ: Highlight, Underline, StrikeOut, Squiggly (QuadPoints tính từ hộp ký tự).
2. Ghi chú: Text (sticky note), FreeText (hộp chữ hiển thị trực tiếp, tiếng Việt), Callout.
3. Vẽ: Ink (InkCanvas → Ink annotation, làm mượt), Square, Circle, Line, Arrow, Polygon, Polyline.
4. Stamp: bộ mẫu "ĐÃ DUYỆT", "BẢN NHÁP", "MẬT", "ĐÃ THANH TOÁN", "ngày giờ + tên người dùng" và stamp ảnh tự tạo.
5. Thuộc tính: màu, độ mờ, độ dày, tác giả (tên trong Cài đặt), thời gian, nội dung. Luôn tạo appearance stream (/AP) để hiển thị giống nhau ở mọi trình đọc.
6. Panel "Bình luận": liệt kê annotation theo trang; lọc theo loại/tác giả/màu; trả lời (reply); trạng thái Accepted/Rejected/Completed; click → nhảy tới; xuất danh sách bình luận ra Markdown/CSV.
7. Chọn, di chuyển, sửa, xóa annotation có sẵn trong file (kể cả do app khác tạo).
8. annotation.flatten: đóng cứng annotation vào nội dung trang, tùy chọn theo loại.
9. Lưu: nếu thay đổi chỉ là annotation → dùng incremental save để không phá chữ ký số đã có.
10. Mọi thao tác annotation undo/redo được (command lưu trạng thái annotation trước/sau).

## Nghiệm thu
- Lưu rồi mở bằng Adobe Acrobat Reader: annotation hiển thị đúng và sửa được.
- Tìm "hoa" (bỏ dấu) khớp "Hòa", "hóa"; tìm trên file 1.000 trang không treo UI và hủy được.
- Thêm highlight vào signed.pdf, lưu: chữ ký cũ vẫn hợp lệ trong Acrobat Reader.

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P7**
> - Highlight trên trang xoay 90° nằm đúng dòng chữ.
> - Copy một đoạn tiếng Việt dán vào Word: đúng dấu, không vỡ dòng lung tung.
> - Mở annotated.pdf (do Acrobat tạo): sửa được ghi chú có sẵn.

### P7 — Sửa nội dung gốc & biểu mẫu

Phase khó nhất về kỹ thuật PDF. Prompt yêu cầu agent phân tích khả thi trước — PDF không có khái niệm "đoạn văn", nên sửa text gốc luôn có giới hạn.

```text
Đọc AGENTS.md và docs/SPEC.md. Phase 7/13 — phase khó nhất. Trước khi code: trình bày phân tích khả thi (làm được gì, giới hạn gì, cách xử lý fallback) + kế hoạch, và chờ tôi duyệt.

## A. Sửa nội dung có sẵn (edit.content) — dùng API page object của PDFium
1. Chế độ "Sửa nội dung": rê chuột hiện khung quanh từng page object (text, image, path).
2. Text: double-click vào khối chữ → ô nhập tại chỗ với nội dung hiện có. Khi áp dụng:
   - Font nhúng có đủ glyph cho chuỗi mới → thay text, giữ font, cỡ, màu, ma trận biến đổi.
   - Thiếu glyph (thường gặp: font đã subset, thiếu ký tự có dấu) → xóa text object cũ, tạo text object mới cùng vị trí/cỡ/màu bằng font Unicode đóng gói gần nhất (serif/sans) và báo cho người dùng biết font đã được thay.
   - Chuỗi mới dài hơn khung cũ: tùy chọn tự xuống dòng trong độ rộng cũ hoặc cho tràn.
   - Ghi rõ giới hạn trong UI: sửa text chỉ chính xác trong phạm vi một khối chữ.
3. Ảnh: thay ảnh (giữ khung), xoay, xóa, trích ảnh ra file ở độ phân giải gốc.
4. Path/vector: chọn, di chuyển, xóa.
5. Sau khi sửa: sinh lại content stream của trang, tạo Revision mới. Test: trích text bằng PdfPig xác nhận nội dung mới có, nội dung cũ không còn.

## B. Biểu mẫu (forms.*)
1. Điền AcroForm qua môi trường form-fill của PDFium: điền trực tiếp trên viewer (text, checkbox, radio, combo, list); Tab đi theo thứ tự tab; tô sáng field; kiểm tra field bắt buộc khi lưu. Form XFA: phát hiện và thông báo không hỗ trợ đầy đủ.
2. forms.design: đặt field mới bằng kéo khung — Text (1 dòng / nhiều dòng, định dạng số/ngày), Checkbox, Radio group, Combo box, List box, Button, Signature field. Panel thuộc tính: tên, giá trị mặc định, bắt buộc, chỉ đọc, font/cỡ (tiếng Việt), tooltip. Ghi field bằng PDFsharp ở tầng dictionary (AcroForm /Fields, widget annotation, /DA, appearance stream). Kiểm tra form tạo ra mở và điền được trong Acrobat Reader.
3. forms.autodetect: đề xuất field từ đường kẻ ngang, ô vuông, chuỗi "....." hoặc "____" — người dùng xác nhận từng đề xuất.
4. Dữ liệu: xuất/nhập JSON và XFDF; trộn dữ liệu từ CSV/Excel → mỗi dòng một file (tên file theo cột chỉ định).
5. forms.flatten: đóng cứng giá trị vào nội dung. forms.reset: xóa toàn bộ giá trị.

## Nghiệm thu
- Sửa một dòng trong file Word-xuất-PDF tiếng Việt: hiển thị đúng, tìm kiếm được chuỗi mới.
- Form tạo bằng PDF_Pro điền được trong Acrobat Reader và Chrome.
- Trộn dữ liệu 100 dòng → 100 file đúng dữ liệu.

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P8**
> - Sửa chữ trong file có font đã subset: app báo đã thay font, kết quả vẫn đọc tốt.
> - Tạo form có radio group, mở bằng Chrome: chỉ chọn được 1 lựa chọn trong nhóm.
> - Flatten form rồi thử sửa trong Acrobat Reader: không sửa được nữa.

### P8 — Bảo mật, xóa vĩnh viễn & chữ ký số

Bảo mật là nơi "sai một ly đi một dặm": mọi tính năng phải có test xác minh kết quả, không chỉ test "chạy không lỗi".

```text
Đọc AGENTS.md và docs/SPEC.md. Phase 8/13. Ở phase này mọi tính năng phải có test XÁC MINH kết quả bảo mật, không chỉ test "chạy không lỗi".

## A. Mật khẩu & quyền (security.encrypt / security.decrypt) — PDFsharp
- Mật khẩu mở (user) + mật khẩu chủ (owner); AES-256 (mặc định) hoặc AES-128 (tương thích cũ); quyền: in (thấp/cao), sửa, copy, chú thích, điền form, ghép trang, trợ năng.
- Đánh giá độ mạnh mật khẩu, cảnh báo mật khẩu yếu; không log mật khẩu; giữ mật khẩu trong bộ nhớ ngắn nhất có thể.
- Gỡ mật khẩu: chỉ khi người dùng nhập đúng mật khẩu.
- Test: file sau mã hóa không mở được khi thiếu mật khẩu, mở được bằng mật khẩu đúng (kiểm bằng cả PDFium và PdfPig).

## B. Xóa thông tin vĩnh viễn — Redaction (security.redact)
1. Đánh dấu vùng: kéo khung, chọn chữ, hoặc "Tìm & đánh dấu" theo mẫu có sẵn: số CCCD (12 chữ số), số điện thoại Việt Nam, email, số tài khoản, ngày sinh, + regex tự định nghĩa; xem lại danh sách trước khi áp dụng.
2. Hai chế độ áp dụng:
   - An toàn tuyệt đối (mặc định): trang có vùng đánh dấu được raster hóa 300 DPI, tô đen vùng, thay trang bằng ảnh; tùy chọn OCR lại phần còn lại để vẫn tìm kiếm được.
   - Giữ vector (nâng cao): dùng page object PDFium — text object giao vùng thì loại các ký tự nằm trong vùng và dựng lại phần còn lại; image object giao vùng thì đổi pixel trong vùng thành đen; path nằm trong vùng thì xóa.
3. BẮT BUỘC hậu kiểm tự động sau khi áp dụng: trích text (PdfPig) trong từng vùng; nếu còn sót bất kỳ ký tự nào → tự chuyển trang đó sang chế độ An toàn tuyệt đối và ghi vào báo cáo.
4. Đồng thời xóa annotation, link, form field, bookmark và metadata có chứa nội dung bị redact.
5. Tùy chọn nhãn trên ô đen (ví dụ "Đã xóa theo quy định") và màu ô.

## C. Làm sạch tài liệu (security.sanitize)
Xóa: metadata Info + XMP, JavaScript (cấp tài liệu và action), file đính kèm, form submit action, link ngoài (tùy chọn), layer đang ẩn, nội dung nằm ngoài CropBox, dữ liệu sau %%EOF và các revision cũ (ghi lại toàn bộ file, không incremental), thumbnail nhúng, dữ liệu riêng của ứng dụng (PieceInfo). Xuất báo cáo chi tiết những gì đã xóa.

## D. Chữ ký số (security.sign) — PDFsharp.Cryptography
1. Nguồn chứng thư: file .pfx/.p12 (nhập mật khẩu) hoặc Windows Certificate Store (CurrentUser\My — gồm cả USB token đã cài driver; PIN do driver/Windows hỏi).
2. Chữ ký hiển thị: chọn vùng trên trang hoặc ký vào Signature field có sẵn; giao diện gồm ảnh chữ ký tay (từ Phase 5), tên, lý do, nơi ký, thời gian. Hoặc chữ ký ẩn.
3. Timestamp RFC 3161 qua TSA URL cấu hình được (có thể tắt).
4. Ký nhiều người: dùng incremental update, không phá chữ ký trước.
5. Ký hàng loạt nhiều file với cùng cấu hình.
6. Test: tạo chứng thư tự ký bằng CertificateRequest của .NET ngay trong test.

## E. Xác minh chữ ký (security.verify) — panel "Chữ ký"
Liệt kê mọi chữ ký: người ký (CN, tổ chức), thời gian ký / timestamp, thuật toán. Kiểm tra: ByteRange phủ toàn bộ file trừ /Contents; hash khớp (SignedCms.CheckSignature); chuỗi chứng thư (X509Chain, kiểm tra thu hồi khi có mạng); tài liệu có bị sửa sau khi ký không (so ByteRange với độ dài file, liệt kê các lần cập nhật sau chữ ký). Hiển thị trạng thái xanh/vàng/đỏ kèm giải thích tiếng Việt dễ hiểu.

## Nghiệm thu
- Redact số CCCD trong pii.pdf: copy, tìm kiếm, trích bằng công cụ khác đều không còn thấy số đó.
- Ký bằng chứng thư test → Acrobat Reader hiển thị chữ ký (cảnh báo "chưa tin cậy" với cert tự ký là bình thường); sửa 1 byte trong file → panel Xác minh báo đỏ.
- Hai người ký lần lượt: cả hai chữ ký đều hợp lệ.

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P9**
> - Sau khi redact, bôi đen vùng ô đen trong Acrobat Reader và copy: không ra chữ nào.
> - Chạy Làm sạch rồi xem Thuộc tính tài liệu: không còn tác giả, ngày tạo, phần mềm tạo.
> - Mở file đã ký bằng Acrobat Reader: thấy panel chữ ký và thông tin người ký.

### P9 — Tối ưu, sửa lỗi & chuyển đổi

Nén "thông minh" theo DPI thực, sửa file hỏng, và các chuyển đổi định dạng thường dùng.

```text
Đọc AGENTS.md và docs/SPEC.md. Phase 9/13. Hoàn thiện tools/fetch-tools.ps1: tải qpdf bản Windows x64 chính thức, kiểm tra SHA-256, đặt vào tools/qpdf; cấu hình để publish đóng gói kèm app.

## A. Nén (optimize.compress)
1. Preset: Màn hình (96 DPI, JPEG chất lượng 60), eBook (150 DPI, 75), In ấn (300 DPI, 85), Lưu trữ (chỉ tối ưu cấu trúc), Tùy chỉnh.
2. Ảnh: tính DPI THỰC của từng ảnh theo kích thước hiển thị trên trang; ảnh vượt ngưỡng → downsample (SkiaSharp, bộ lọc chất lượng cao) rồi nén lại JPEG; ảnh đen trắng 1-bit (scan văn bản) giữ 1-bit (Flate hoặc CCITT G4 nếu khả thi), không chuyển sang JPEG; giữ kênh alpha (SMask); ảnh trùng lặp (hash nội dung) → dùng chung một XObject. Luôn nén từ dữ liệu ảnh gốc, không nén chồng.
3. Font: bỏ font nhúng trùng lặp; không bỏ font cần cho hiển thị.
4. Cấu trúc: xóa object không dùng, nén stream mức cao, object streams + xref stream qua qpdf.
5. Hiển thị trước/sau: dung lượng, % giảm; cho xem thử chất lượng 1 trang trước khi chạy cả file; nếu file sau nén lớn hơn → giữ bản gốc và báo.

## B. Cấu trúc
- optimize.linearize: Fast Web View (qpdf --linearize).
- optimize.repair: sửa file hỏng — thử lần lượt: PDFium mở + lưu lại; qpdf khôi phục xref; PdfPig chế độ lenient. Báo cáo phương án nào thành công.
- optimize.grayscale: chuyển sang thang xám; nêu rõ đánh đổi giữa raster hóa và chuyển không gian màu.

## C. Chuyển đổi
1. convert.toImages: PNG / JPEG / TIFF (TIFF nhiều trang; LZW cho màu, G4 cho đen trắng), chọn DPI, khoảng trang, nền trong suốt cho PNG.
2. convert.fromImages: JPG/PNG/TIFF nhiều trang/BMP → PDF; sắp xếp tự nhiên theo tên (img2 trước img10); khổ: theo ảnh / A4 vừa khít / A4 có lề; xoay theo EXIF; JPEG giữ nguyên dữ liệu (không nén lại).
3. convert.toText: TXT / Markdown giữ thứ tự đọc hợp lý (dùng thuật toán phân đoạn trang và thứ tự đọc của PdfPig), tùy chọn tách theo trang.
4. convert.officeToPdf: tự dò LibreOffice đã cài (registry + đường dẫn mặc định, cho chọn tay trong Cài đặt); gọi soffice --headless --convert-to pdf với hồ sơ người dùng RIÊNG cho mỗi lần chạy (-env:UserInstallation) để chạy song song không xung đột; có timeout; hỗ trợ docx/xlsx/pptx/odt/rtf. Không có LibreOffice → hướng dẫn cài.
5. convert.toPdfA: PDF/A-2b (và 1b): nhúng toàn bộ font, OutputIntent sRGB, XMP metadata, loại JavaScript/mã hóa/đa phương tiện; dùng hỗ trợ PDF/A của PDFsharp khi phù hợp. Kiểm định: nếu người dùng đã cài veraPDF thì gọi CLI và hiển thị báo cáo (không đóng gói veraPDF).

## Nghiệm thu
- File scan 50 trang ~80 MB → preset eBook còn dưới 15 MB, chữ vẫn đọc rõ.
- broken.pdf được sửa và mở được.
- Ảnh JPEG → PDF: stream ảnh trong PDF giống hệt byte file JPEG gốc.

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P10**
> - Nén cùng một file 2 lần liên tiếp bằng preset eBook: lần 2 không làm ảnh xấu thêm.
> - Chuyển file Word tiếng Việt sang PDF: đúng font, đúng dấu.
> - Mở file đã linearize trên trình duyệt qua mạng: trang đầu hiện trước khi tải xong.

### P10 — OCR & bộ công cụ scan

Biến file scan thành PDF tìm kiếm được với chất lượng tiếng Việt cao, KHÔNG làm giảm chất lượng ảnh gốc.

```text
Đọc AGENTS.md và docs/SPEC.md (mục "Tiếng Việt"). Phase 10/13. Mục tiêu: PDF scan → PDF tìm kiếm được, tiếng Việt chính xác, ảnh gốc giữ nguyên 100%.

## A. Hạ tầng
1. fetch-tools.ps1: tải Tesseract 5 bản Windows x64 (dạng portable/đã giải nén) + tessdata_best cho vie, eng và osd; kiểm tra SHA-256; đặt vào tools/tesseract. Cài đặt cho phép chọn ngôn ngữ và bộ dữ liệu fast/best.
2. OcrEngine (PdfPro.Ocr) gọi tesseract.exe qua IExternalToolRunner (không shell), timeout theo trang, hủy được.

## B. Pipeline OCR (ocr.run) — theo trang, song song (mặc định số nhân CPU − 1, chỉnh được)
1. Trang đã có lớp chữ: tùy chọn bỏ qua / OCR lại / xóa lớp chữ cũ rồi OCR.
2. Render trang 300 DPI bằng PDFium.
3. Tiền xử lý CHỈ cho ảnh đưa vào OCR (OpenCvSharp): xác định hướng 0/90/180/270 (tesseract OSD), chống nghiêng ±10°, khử nhiễu, nhị phân hóa thích nghi, tăng tương phản.
4. Chạy tesseract xuất PDF chỉ chứa lớp chữ (cấu hình textonly_pdf) hoặc hOCR.
5. Ghép lớp chữ vô hình (text render mode 3) lên trang GỐC — PDFsharp vẽ trang text-only như form XObject đè lên trang gốc, đúng tỉ lệ và góc xoay → ảnh gốc không bị đụng tới.
6. Tùy chọn áp deskew/xoay vào trang thật ("Làm thẳng trang") nếu người dùng muốn.
7. Xuất thêm TXT/hOCR; báo cáo độ tin cậy trung bình từng trang, đánh dấu trang dưới ngưỡng.
8. Progress theo trang, ước lượng thời gian còn lại; hủy giữa chừng thì tài liệu giữ nguyên.

## C. Công cụ scan (scan.*)
- scan.removeBlankPages: phát hiện trang trắng (ngưỡng mực, bỏ lề, chịu được bóng gáy sách), xem trước danh sách và xác nhận.
- scan.deskew: làm thẳng trang không cần OCR.
- scan.autoRotate: xoay đúng chiều theo OSD.
- scan.cleanBorders: xóa viền đen do máy scan.
- scan.splitByBlank: dùng lại chế độ tách theo trang trắng của Phase 4.

## Nghiệm thu
- scan-vi.pdf sau OCR: tìm được "Ủy ban nhân dân", copy ra đúng dấu.
- Stream ảnh gốc của mọi trang không đổi (so byte).
- 100 trang chạy song song nhanh hơn rõ rệt so với tuần tự — ghi số liệu benchmark vào báo cáo.

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P11**
> - Bôi đen chữ trên trang scan sau OCR: vùng chọn khớp vị trí chữ trên ảnh.
> - Trang scan bị lộn ngược: autoRotate xoay đúng.
> - Hủy OCR ở giữa: app không treo, tài liệu không đổi.

### P11 — So sánh, trích xuất & cấu trúc tài liệu

Tính năng "nâng cao" thường chỉ có ở bản trả phí: so sánh hai phiên bản, trích bảng sang Excel, sửa cấu trúc tài liệu.

```text
Đọc AGENTS.md và docs/SPEC.md. Phase 11/13.

## A. So sánh 2 tài liệu (compare.*) — chế độ xem song song
1. So sánh văn bản: trích text theo trang (PdfPig, NFC), diff cấp từ (DiffPlex), căn trang theo NỘI DUNG chứ không chỉ theo số trang (xử lý chèn/xóa trang); tô xanh phần thêm, đỏ phần xóa, vàng phần sửa; panel danh sách thay đổi, click → nhảy tới cả hai bên.
2. So sánh hình ảnh: render cùng DPI, so pixel có ngưỡng, phủ màu vùng khác biệt; chế độ lớp chồng và chế độ thanh trượt.
3. Cuộn và zoom đồng bộ hai bên (bật/tắt được).
4. Xuất báo cáo so sánh ra PDF (mỗi thay đổi một dòng, kèm số trang).

## B. Trích xuất
1. extract.tables: phát hiện bảng bằng tabula-sharp (lattice cho bảng có kẻ, stream cho bảng không kẻ); người dùng chỉnh vùng bảng trên trang; xem trước lưới; xuất Excel (ClosedXML, mỗi bảng một sheet) hoặc CSV UTF-8 có BOM (Excel mở đúng tiếng Việt).
2. extract.images: xuất mọi ảnh nhúng với định dạng gốc, lọc ảnh nhỏ hơn N px, bỏ trùng.
3. extract.attachments: liệt kê, mở, lưu, thêm, xóa file đính kèm.
4. extract.fonts: danh sách font, trạng thái nhúng / subset / không nhúng — hữu ích khi kiểm tra trước in.

## C. Cấu trúc tài liệu
1. Trình sửa bookmark: thêm từ trang hiện tại hoặc vùng chữ đang chọn, đổi tên tại chỗ, kéo-thả đổi thứ tự/cấp, màu và kiểu chữ; tự tạo bookmark từ tiêu đề (theo cỡ chữ lớn hoặc regex như "^Chương [IVXLC]+").
2. Metadata: Title, Author, Subject, Keywords, Creator, Producer, ngày tạo/sửa; đồng bộ Info ↔ XMP; thiết lập khi mở (trang đầu, mức zoom, hiện panel bookmark, tiêu đề cửa sổ = Title).
3. Page labels: nhãn trang (i–x cho phần mở đầu, 1… cho nội dung); viewer hiển thị theo nhãn.
4. Liên kết: tạo link tới trang hoặc URL, sửa, xóa; tự nhận diện URL/email trong text để tạo link.

## Nghiệm thu
- contract-v1 vs contract-v2 (bản sau chèn 1 trang + sửa 3 chỗ): báo đúng 4 thay đổi, không báo sai toàn bộ các trang sau chỗ chèn.
- Bảng trong tables.pdf xuất Excel đúng số cột/hàng, tiếng Việt không lỗi.

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P12**
> - So sánh một file với chính nó: báo 0 khác biệt.
> - Tự tạo bookmark theo regex cho một giáo trình: cây mục lục đúng cấp.
> - Mở CSV xuất ra bằng Excel: không bị lỗi font tiếng Việt.

### P12 — Tự động hóa: Command Palette, workflow, chạy lô, hot folder, CLI

Nhờ thiết kế `IPdfOperation` từ đầu, phase này chủ yếu là "lắp ráp" — và là thứ tách PDF_Pro khỏi phần lớn công cụ miễn phí.

```text
Đọc AGENTS.md và docs/SPEC.md (mục "Ba khái niệm lõi"). Phase 12/13. Nhờ IPdfOperation, phase này chủ yếu là lắp ráp — không viết lại logic PDF.

## A. Danh mục thao tác (Operation Catalog)
1. OperationRegistry tự thu thập mọi IPdfOperation qua DI; metadata mỗi thao tác: Id, tên/mô tả (resource vi/en), nhóm, icon, Params type, có hỗ trợ chạy lô không, số input (1 hay nhiều).
2. Tự sinh form tham số từ Params record (thuộc tính + DataAnnotations: Range, Required, RegularExpression; enum → combo; bool → toggle) — dùng chung cho hộp thoại, workflow builder và kiểm tra tham số CLI.

## B. Command Palette (Ctrl+K)
Tìm mờ, không dấu mọi lệnh / thao tác / cài đặt; hiển thị phím tắt; Enter để chạy; lịch sử lệnh gần dùng.

## C. Workflow (kiểu Action Wizard)
1. Workflow = danh sách bước {operationId, params, enabled} + cài đặt đầu ra (ghi đè / thư mục mới / hậu tố tên). Lưu JSON tại %LOCALAPPDATA%\PdfPro\workflows; xuất/nhập file .pdfflow để chia sẻ.
2. Trình thiết kế: kéo-thả thêm bước, sắp xếp, bật/tắt, form tham số tự sinh; kiểm tra hợp lệ cả chuỗi trước khi chạy (ví dụ: mã hóa phải là bước cuối; cảnh báo bước làm mất hiệu lực chữ ký số).
3. Mẫu dựng sẵn: "Số hóa hồ sơ" (OCR → xóa trang trắng → nén eBook → PDF/A-2b); "Phát hành tài liệu" (watermark → đánh số trang → làm sạch → mã hóa); "Chuẩn bị in" (chuẩn hóa A4 → booklet).
4. Chạy trên tài liệu đang mở hoặc chạy lô.

## D. Chạy lô (batch.run)
Chọn file/thư mục (đệ quy, lọc theo mẫu tên), chọn thao tác hoặc workflow; chạy song song có giới hạn; bảng tiến độ từng file (chờ / đang chạy / xong / lỗi + lý do); một file lỗi không dừng cả lô; tạm dừng/hủy; xuất log CSV; chạy lại riêng các file lỗi.

## E. Hot folder (watch.folder)
Theo dõi thư mục (FileSystemWatcher + quét định kỳ phòng sự kiện bị mất); chờ file ghi xong (kích thước ổn định + mở độc quyền được); chạy workflow; chuyển file gốc sang _done hoặc _error; chạy nền khi thu nhỏ xuống khay hệ thống; tùy chọn khởi động cùng Windows.

## F. CLI (PdfPro.Cli → pdfpro.exe) — dùng chung Operations, không phụ thuộc WPF
Lệnh: merge, split, rotate, compress, ocr, encrypt, decrypt, watermark, stamp, sign, redact (theo mẫu), convert, info (xuất JSON), run-workflow <file.pdfflow> <inputs…>, list-ops. Mã thoát chuẩn (0 = thành công, khác 0 theo loại lỗi); --json cho đầu ra máy đọc được; --quiet; progress ghi ra stderr. Viết docs/CLI.md kèm ví dụ.

## Nghiệm thu
- Workflow "Số hóa hồ sơ" trên thư mục 50 file có 1 file hỏng: 49 thành công, 1 báo lỗi rõ ràng.
- Cùng workflow chạy bằng CLI cho kết quả tương đương UI (cùng số trang, cùng text trích ra).

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra trước khi sang P13**
> - Thả 10 file vào hot folder trong khi đang copy: không file nào bị xử lý khi chưa copy xong.
> - `pdfpro info file.pdf --json` trả JSON hợp lệ; `echo %ERRORLEVEL%` khác 0 khi file không tồn tại.
> - Xuất một workflow ra .pdfflow, nhập lại trên máy khác: chạy y hệt.

### P13 — Hoàn thiện, hiệu năng, bảo mật & phát hành

Đưa app lên chuẩn phát hành: không crash trước file độc hại, đo được hiệu năng, có installer và tự cập nhật.

```text
Đọc AGENTS.md và docs/SPEC.md. Phase 13/13 — đưa app lên chuẩn phát hành.

## A. Cô lập engine (PdfPro.Worker)
File PDF độc hại hoặc hỏng nặng có thể làm crash thư viện native. Tạo process PdfPro.Worker chạy các thao tác rủi ro (OCR, repair, render file lạ khi bật Chế độ an toàn, file tải từ Internet có Mark-of-the-Web). Giao tiếp qua named pipe + StreamJsonRpc. Worker chết → app báo lỗi, tạo worker mới, không mất dữ liệu đang chỉnh sửa. Giới hạn bộ nhớ và thời gian mỗi tác vụ bằng Job Object. Bật/tắt trong Cài đặt.

## B. Kiểm thử chống chịu
1. Bộ torture test: xref hỏng, trang 0×0, ảnh 30.000×30.000 px, 10.000 trang, font lỗi, vòng lặp tham chiếu, decompression bomb → app không crash, không treo, báo lỗi đúng.
2. Fuzzing nhẹ: đột biến ngẫu nhiên byte của file mẫu rồi chạy mở + render + lưu; chạy trong CI theo lịch ban đêm.
3. Test UI tự động các luồng chính bằng FlaUI.

## C. Hiệu năng (PdfPro.Benchmarks — BenchmarkDotNet)
Đo: mở file 1.000 trang; render 1 trang ở 3 mức zoom; materialize plan 1.000 trang; nén 50 trang scan; OCR 10 trang. Ngân sách: khởi động dưới 1,5 s; trang đầu của file lớn hiện dưới 2 s; RAM khi xem file 1.000 trang dưới 800 MB. Profile và sửa điểm nghẽn (ưu tiên: cấp phát bitmap — dùng ArrayPool; render thừa; giữ PdfiumGate quá lâu).

## D. Trải nghiệm & trợ năng
Bảng phím tắt đầy đủ (F1); tooltip mọi nút; AutomationProperties cho trình đọc màn hình; điều hướng hoàn toàn bằng bàn phím; DPI cao và nhiều màn hình khác DPI (PerMonitorV2); chế độ tương phản cao; hướng dẫn lần đầu 4 bước.

## E. Phát hành
1. Velopack: Setup.exe, tự cập nhật (kênh stable/beta); liên kết file .pdf là TÙY CHỌN khi cài; menu chuột phải Explorer "Mở bằng PDF_Pro" và "Gộp bằng PDF_Pro" (tùy chọn).
2. Publish self-contained win-x64 và win-arm64, ReadyToRun; chỉ bật trimming nếu đã test kỹ (WPF dùng nhiều reflection).
3. Ký mã installer và exe: để sẵn cấu hình, ghi hướng dẫn thêm chứng thư.
4. THIRD_PARTY_NOTICES.md đầy đủ (thư viện, font, tessdata, qpdf); trang "Giới thiệu" trong app hiển thị phiên bản + license.
5. Không telemetry mặc định; báo lỗi = nút "Mở thư mục log" + "Sao chép thông tin lỗi".
6. CHANGELOG.md, README (ảnh chụp màn hình, tính năng, cách build từ nguồn); GitHub Actions tạo release khi push tag v*.

## Nghiệm thu
- Cài trên máy Windows sạch (chưa có .NET): chạy được.
- Cập nhật tự động từ 1.0.0 lên 1.0.1 thành công.
- Bộ torture test: không có crash.

Làm theo quy trình chuẩn trong AGENTS.md.
```

> [!TIP]
> **Kiểm tra cuối cùng trước khi phát hành**
> - Cài bản Setup.exe trên máy ảo Windows sạch, chạy hết các luồng chính trong 15 phút.
> - Gỡ cài đặt: không để lại rác ngoài thư mục dữ liệu người dùng (và có tùy chọn xóa luôn).
> - Đọc lại THIRD_PARTY_NOTICES.md: không có thư viện AGPL/GPL nào lọt vào.


## Prompt tiện ích

Bốn prompt dùng lặp lại trong suốt dự án. Thay phần trong ngoặc vuông `[...]` bằng thông tin thật trước khi gửi.

### U1 — Debug có hệ thống

Dùng khi gặp lỗi. Điểm mấu chốt: bắt agent viết test tái hiện lỗi TRƯỚC khi sửa — tránh kiểu "sửa mò" làm hỏng chỗ khác.

```text
Có lỗi cần xử lý.
- Hiện tượng: [mô tả]
- Bước tái hiện: [1… 2… 3…]
- Kỳ vọng: [...]  |  Thực tế: [...]
- Log / stack trace: [dán vào đây]
- File mẫu gây lỗi: samples/[tên file]

Yêu cầu: KHÔNG sửa code ngay.
1. Đọc code liên quan, nêu 2–3 giả thuyết nguyên nhân, xếp theo khả năng.
2. Viết test tái hiện lỗi (test phải đỏ).
3. Kiểm chứng giả thuyết bằng log/debug, nói rõ giả thuyết nào đúng và bằng chứng.
4. Sửa tối thiểu đúng gốc rễ; test tái hiện chuyển xanh, toàn bộ test khác vẫn xanh.
5. Giải thích nguyên nhân bằng tiếng Việt dễ hiểu, và đề xuất dòng bổ sung vào AGENTS.md (nếu là lỗi có thể lặp lại).
```

### U2 — Review cuối phase

Chạy trước khi merge mỗi phase. Agent đóng vai reviewer khó tính, soi đúng những lỗi hay gặp nhất của dự án này.

```text
Review toàn bộ thay đổi của phase vừa xong (git diff main...HEAD) như một senior reviewer khó tính, đối chiếu AGENTS.md và docs/SPEC.md. Soi kỹ:
- Vi phạm luật kiến trúc / chiều phụ thuộc giữa project.
- Gọi PDFium ngoài PdfiumGate; rò rỉ handle; thiếu Dispose.
- Chặn UI thread; thiếu CancellationToken; thiếu progress.
- Chuyển tọa độ không qua PageCoordinateMapper.
- Chuỗi hard-code; nuốt exception; thông báo lỗi khó hiểu.
- Thay đổi tài liệu không undo được; ghi đè file gốc không an toàn.
- Thiếu test cho nhánh lỗi; test chỉ kiểm "không throw".
- Package có license không phù hợp.
Xuất bảng: Mức độ (Nghiêm trọng / Nên sửa / Gợi ý) · File:dòng · Vấn đề · Cách sửa. Sau đó hỏi tôi muốn sửa những mục nào.
```

### U3 — Tiếp tục phiên bị ngắt

Dùng khi phiên chat bị đứt giữa chừng, hết context, hoặc bạn quay lại sau vài ngày.

```text
Phiên trước bị ngắt giữa Phase [N]. Hãy:
1. Đọc AGENTS.md, docs/SPEC.md, phần Phase [N] trong docs/PROMPTS.md.
2. Chạy git log --oneline -15 và git status; đọc các file đã thay đổi.
3. Chạy build + test để biết trạng thái thật.
4. Tóm tắt: đã xong gì, đang dở gì, còn lại gì của Phase [N] (đối chiếu từng mục trong prompt phase).
5. Đề xuất bước tiếp theo và chờ tôi xác nhận.
```

### U4 — Thêm tính năng ngoài kế hoạch

Giữ kiến trúc sạch khi bạn nảy ra ý tưởng mới giữa chừng.

```text
Tôi muốn thêm tính năng: [mô tả]. Trước khi code:
1. Xác định đây là IPdfOperation thuộc nhóm nào, Id là gì, Params gồm những gì, PlanOnly hay Content.
2. Thư viện nào trong stack hiện có làm được. Không thêm thư viện mới nếu không cần; nếu cần thì nêu tên, license, lý do.
3. Cập nhật docs/SPEC.md (ma trận tính năng).
4. Kế hoạch UI + test (golden-file trên samples/).
Chờ tôi duyệt rồi mới code.
```

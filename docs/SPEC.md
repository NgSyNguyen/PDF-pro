# PDF_Pro — Đặc tả kỹ thuật (SPEC)

> Nguồn sự thật của dự án. Cập nhật lần cuối: 27/10/2026. Khi thiết kế thay đổi, cập nhật file này trước rồi mới code.

## Sản phẩm & ma trận tính năng

**PDF_Pro** nhắm tới nhóm tính năng của các trình biên tập PDF trả phí (Adobe Acrobat Pro, Foxit PDF Editor, PDF-XChange Editor) nhưng xây hoàn toàn bằng thư viện mã nguồn mở có license thân thiện thương mại. "Pro" không chỉ là nhiều nút bấm, mà nằm ở ba trụ cột kiến trúc:

1. **Tức thì:** sắp xếp, xoay, xóa, chèn trang trên file 1.000 trang phản hồi ngay — nhờ "tài liệu ảo" PagePlan, chỉ ghi file khi lưu.
2. **An toàn:** mọi thay đổi undo/redo được, file gốc không bị đụng tới cho đến khi lưu, lưu kiểu nguyên tử, có khôi phục sau crash.
3. **Tự động hóa được:** mọi tính năng là một Operation dùng chung cho nút bấm, workflow, chạy lô, hot folder và dòng lệnh.

### Ma trận tính năng

Mức: **MVP** = bản dùng được đầu tiên · **Pro** = ngang app trả phí · **Adv** = nâng cao, tạo khác biệt.

| Nhóm | Tính năng | Mức | Phase |
|---|---|---|---|
| Xem & điều hướng | Đa tab, cuộn liên tục, zoom giữ điểm dưới chuột, thumbnail, bookmark, mở file có mật khẩu, kéo-thả, file gần đây, single-instance, in ấn | MVP | P2 |
| Tổ chức trang | Kéo-thả sắp xếp nhiều trang, xoay, xóa, nhân bản, chèn trang trắng, chèn trang từ file khác, trích trang, cắt lề (crop), đổi khổ giấy, undo/redo, khôi phục sau crash | MVP | P3 |
| Gộp & tách | Gộp nhiều file giữ bookmark + mục lục tự động; tách theo khoảng, mỗi N trang, theo bookmark, theo dung lượng, theo trang trắng phân cách, theo văn bản (regex), theo trang đầu được đánh dấu của từng văn bản; trộn trang lẻ/chẵn; N-up; booklet; poster | Pro | P4 |
| Chèn & đóng dấu | Thêm chữ tiếng Việt, ảnh, hình, che trắng, chữ ký tay dạng ảnh; watermark chữ/ảnh (layer gỡ được); header/footer; đánh số trang; đánh số Bates nhiều file; nền trang | Pro | P5 |
| Chú thích & tìm kiếm | Chọn/copy chữ; tìm kiếm có/không dấu, regex; highlight, gạch chân, gạch ngang, ghi chú, hộp chữ, vẽ tay, hình học, con dấu; panel bình luận có trả lời & trạng thái; flatten | Pro | P6 |
| Sửa nội dung gốc | Sửa khối chữ có sẵn (giữ font khi đủ glyph), thay/xóa/trích ảnh, xóa đối tượng vector | Adv | P7 |
| Biểu mẫu | Điền AcroForm; thiết kế field (text, checkbox, radio, combo, list, button, chữ ký); tự phát hiện ô điền; xuất/nhập JSON, XFDF; trộn dữ liệu CSV/Excel ra nhiều file; flatten | Pro | P7 |
| Bảo mật | Mật khẩu AES-256 + phân quyền; xóa vĩnh viễn (true redaction) có hậu kiểm, tìm theo mẫu CCCD/SĐT/email/STK; làm sạch tài liệu; ký số .pfx hoặc Windows cert store/USB token + timestamp; ký nhiều người; xác minh chữ ký | Pro | P8 |
| Tối ưu & sửa lỗi | Nén theo preset (DPI thực, nén lại JPEG, gộp ảnh trùng, tối ưu cấu trúc), linearize, sửa file hỏng, chuyển thang xám | Pro | P9 |
| Chuyển đổi | PDF → PNG/JPEG/TIFF; ảnh → PDF; PDF → TXT/Markdown; Word/Excel/PowerPoint → PDF; PDF/A-2b | Pro | P9 |
| OCR & scan | OCR tiếng Việt + Anh tạo lớp chữ ẩn (giữ nguyên ảnh gốc), xử lý song song; tự xoay, chống nghiêng, xóa trang trắng, xóa viền đen | Pro | P10 |
| So sánh & trích xuất | So sánh văn bản (căn trang theo nội dung) và hình ảnh; trích bảng → Excel/CSV; trích ảnh, file đính kèm; sửa bookmark, metadata, page label, liên kết | Adv | P11 |
| Tự động hóa | Command Palette (Ctrl+K); workflow builder với mẫu dựng sẵn; chạy lô; hot folder chạy nền; ứng dụng dòng lệnh `pdfpro` | Adv | P12 |
| Phát hành | Engine cô lập trong process riêng, torture test, benchmark, trợ năng, installer + tự cập nhật, file association | Pro | P13 |

> [!IMPORTANT]
> **Có MVP chỉ sau 4 phase**
> Xong P1–P4 là có app dùng hằng ngày được (xem, sắp xếp, gộp, tách). P5–P6 đưa app ngang các công cụ miễn phí phổ biến. P7–P13 là phần "pro" thật sự: sửa nội dung, ký số, redaction, OCR, workflow.

## Kiến trúc kỹ thuật

### Stack công nghệ

| Thành phần | Lựa chọn | License | Vai trò |
|---|---|---|---|
| Runtime | .NET 10 LTS, C# bản mới nhất | MIT | Được hỗ trợ đến 11/2028 |
| Giao diện | WPF + CommunityToolkit.Mvvm + WPF-UI (Fluent) | MIT | MVVM, giao diện Windows 11, sáng/tối |
| Hạ tầng app | Microsoft.Extensions.Hosting, Serilog | MIT / Apache-2.0 | DI, cấu hình, log |
| Render & sửa cấp thấp | PDFium qua PDFiumCore (binary từ pdfium-binaries) | Apache-2.0 / BSD | Render, text theo ký tự, annotation, form, page object, lưu incremental |
| Cấu trúc & overlay | PDFsharp **6.2.4 (ghim cứng)** và PDFsharp.Cryptography | MIT | Ghép/tách, vẽ lên trang, AES-256, ký số CMS, PDF/A. Bản 6.5+ đổi sang AGPL nên cấm nâng cấp (test trong Operations.Tests canh chừng); `System.Security.Cryptography.Pkcs` được ghim 10.0.x để vá lỗ hổng của bản 8.0.1 mà PDFsharp kéo theo |
| Đọc text có tọa độ | PdfPig | Apache-2.0 | Trích từ + hộp bao: tìm kiếm, redaction, so sánh |
| Xử lý ảnh | SkiaSharp, OpenCvSharp4 | MIT / Apache-2.0 | Nén lại ảnh, deskew, phát hiện trang trắng |
| OCR | Tesseract 5 (CLI đóng gói, tessdata vie + eng) | Apache-2.0 | Lớp chữ ẩn cho file scan |
| Tối ưu/sửa file | qpdf (CLI đóng gói) | Apache-2.0 | Linearize, object streams, khôi phục xref |
| Office → PDF | LibreOffice (người dùng tự cài) | MPL-2.0 | Gọi headless, app tự dò |
| So sánh / bảng | DiffPlex, tabula-sharp, ClosedXML | Apache-2.0 / MIT | Diff văn bản, trích bảng, xuất Excel |
| Tiện ích UI & CLI | gong-wpf-dragdrop, System.CommandLine | BSD-3 / MIT | Kéo-thả, ứng dụng dòng lệnh |
| Đóng gói | Velopack | MIT | Installer, tự cập nhật |
| Kiểm thử | xUnit v3 (chạy trên Microsoft.Testing.Platform, bật trong global.json), Shouldly, NSubstitute, BenchmarkDotNet, FlaUI | Apache-2.0 / BSD / MIT | Unit, golden-file, hiệu năng, UI |

> [!CAUTION]
> **Bẫy license — KHÔNG dùng**
> - **iText, Ghostscript, MuPDF**: license AGPL — phân phối app là phải công bố toàn bộ mã nguồn.
> - **FluentAssertions từ v8**: đã chuyển sang license thương mại; dùng Shouldly hoặc AwesomeAssertions.
> - SDK thương mại (Syncfusion, Aspose, Apryse, Foxit SDK) rất mạnh nhưng phải mua license; chỉ cân nhắc khi bán sản phẩm và luôn kiểm tra điều kiện license hiện hành trước.

> [!NOTE]
> **WPF chỉ chạy trên Windows**
> Nếu sau này cần macOS/Linux, chỉ thay project giao diện bằng Avalonia. Các project Core, Pdfium, Operations, Ocr không phụ thuộc WPF — đó chính là lý do tách project ngay từ đầu.

### Cấu trúc solution

```text
PdfPro/
├─ AGENTS.md                     // luật cho AI agent (P0)
├─ CLAUDE.md                     // nạp @AGENTS.md và @docs/PROGRESS.md (cho Claude Code)
├─ Directory.Build.props         // net10.0, Nullable, TreatWarningsAsErrors
├─ Directory.Packages.props      // version NuGet quản lý tập trung
├─ global.json                   // ghim .NET SDK 10
├─ PdfPro.slnx
├─ THIRD_PARTY_NOTICES.md        // license mọi thư viện, font, tessdata
├─ docs/      SPEC.md · PROMPTS.md · PROGRESS.md (tiến độ, bàn giao) · adr/
├─ samples/                      // PDF mẫu để test (dữ liệu giả)
├─ assets/fonts/                 // font OFL đóng gói: Noto Sans/Serif, Be Vietnam Pro (nhúng vào PdfPro.Operations); Charm, Dancing Script (chữ ký gõ tên, chép cạnh exe)
├─ tools/                        // qpdf, tesseract + tessdata (tải bằng script)
├─ src/
│  ├─ PdfPro.Core/               // domain thuần: Session, PagePlan, Command, Operation
│  ├─ PdfPro.Pdfium/             // wrapper an toàn: SafeHandle, PdfiumGate 1 luồng
│  ├─ PdfPro.Operations/         // các thao tác PDF (PDFsharp, PdfPig, PDFium, qpdf)
│  ├─ PdfPro.Ocr/                // Tesseract + tiền xử lý ảnh
│  ├─ PdfPro.Infrastructure/     // file, settings, tiến trình ngoài, font
│  ├─ PdfPro.App/                // WPF + MVVM
│  ├─ PdfPro.Cli/                // pdfpro.exe
│  └─ PdfPro.Worker/             // (P13) process cô lập cho thao tác rủi ro
└─ tests/
   ├─ PdfPro.Core.Tests/
   ├─ PdfPro.Operations.Tests/   // golden-file trên samples/ + test chiều phụ thuộc
   ├─ PdfPro.Infrastructure.Tests/ // tiến trình ngoài, settings, ghi file nguyên tử
   ├─ PdfPro.Pdfium.Tests/       // wrapper PDFium: gate, handle, render so ảnh tham chiếu (Golden/)
   ├─ PdfPro.App.Tests/          // test ViewModel, trình xem, in (ghi XPS), bảng chuỗi vi/en
   ├─ PdfPro.App.UiTests/        // (P3, P4) nghiệm thu trên app thật bằng FlaUI; chỉ chạy tay (PDFPRO_UI_TESTS=1)
   ├─ PdfPro.SampleGenerator/    // sinh lại samples/ (PDFsharp), không phải project test
   ├─ PdfPro.TestTools.FakeTool/ // exe giả lập công cụ ngoài, chỉ dùng cho test
   └─ PdfPro.Benchmarks/
```

Quy tắc phụ thuộc một chiều: **App / Cli → Operations, Ocr, Infrastructure → Pdfium → Core**. `PdfPro.Core` không biết gì về WPF hay thư viện PDF, nên test cực nhanh và dùng lại được cho CLI.

### Ba khái niệm lõi

**(1) DocumentSession và Revision.** Mở file nghĩa là sao chép vào thư mục phiên `%LOCALAPPDATA%\PdfPro\sessions\{id}\`. File gốc không bị đụng tới cho đến khi người dùng bấm Lưu. Mỗi thao tác nội dung (watermark, OCR, redaction…) tạo một revision mới (`r0001.pdf`, `r0002.pdf`…); `manifest.json` ghi toàn bộ lịch sử để khôi phục sau crash.

**(2) PagePlan — tài liệu ảo.** Danh sách `PageRef(SourceId, SourcePageIndex, RotationDelta, CropBox?)`. Sắp xếp, xoay, xóa, nhân bản, chèn trang chỉ sửa danh sách này (bất biến, rất nhẹ) nên phản hồi tức thì. Chỉ khi lưu, hoặc ngay trước một thao tác nội dung, Materializer mới "vật chất hóa" plan thành file PDF thật.

**(3) Operation + Command.** Mọi tính năng là một `IPdfOperation` với tham số là record bất biến, validate được, serialize JSON được. Một engine duy nhất phục vụ nút bấm, undo/redo, workflow, chạy lô, hot folder và CLI.

```csharp
// PdfPro.Core/Operations/IPdfOperation.cs
public enum OperationKind { PlanOnly, Content, Export }

public interface IPdfOperation
{
    string Id { get; }                    // "organize.rotate", "security.encrypt"
    OperationKind Kind { get; }
    Type ParamsType { get; }              // record tham số, có DataAnnotations
    OperationValidationResult Validate(object parameters, DocumentInfo doc);
    Task<OperationResult> ExecuteAsync(OperationContext ctx, object parameters,
        IProgress<OperationProgress> progress, CancellationToken ct);
}

// Undo/Redo cho mọi thay đổi của người dùng. TContext = DocumentSession từ Phase 3.
public interface IUndoableCommand<in TContext>
{
    string Description { get; }           // "Xoay 3 trang" -> hiện trong menu Hoàn tác
    void Do(TContext context);
    void Undo(TContext context);
}
```

- `OperationValidationResult` (không đặt tên `ValidationResult`) để khỏi trùng với `System.ComponentModel.DataAnnotations.ValidationResult` mà phần kiểm tra tham số cũng dùng.
- Tham số record dạng positional phải gắn attribute vào property: `[property: Range(1, 100)] int Page`, nếu không `Validator` sẽ không thấy.
- `UndoRedoStack<TContext>`: giới hạn độ sâu, xóa nhánh redo khi có lệnh mới, `CompositeCommand` gộp nhiều lệnh thành 1 bước (lỗi giữa chừng thì tự hoàn tác phần đã chạy), "điểm lưu" (`MarkSavePoint`/`IsAtSavePoint`) để biết tài liệu chưa lưu.

### Luồng xử lý & đa luồng

- UI thread chỉ hiển thị. Mọi thao tác trên 100 ms chạy nền với `IProgress` + `CancellationToken`; panel Tác vụ hiển thị tiến độ và nút Hủy.
- **PDFium không thread-safe**: mọi lời gọi đi qua `PdfiumGate` — một luồng chuyên dụng. Bitmap render xong được `Freeze()` để chuyển sang UI thread an toàn.
- Cache render LRU theo ngân sách byte (mặc định 512 MB, chỉnh 128–4096 MB trong Cài đặt) dùng chung cho mọi tab + cache thumbnail trên đĩa (chi tiết ở mục Trình xem).
- Render 2 nấc: ảnh mờ khi đang zoom/cuộn, ảnh nét sau khi dừng; hủy render các trang đã cuộn qua.
- Lưu an toàn: ghi file tạm cùng thư mục → flush → `File.Replace` (nguyên tử). Mất điện giữa chừng cũng không hỏng file gốc.
- Tiến trình ngoài (qpdf, tesseract, soffice) chạy qua `IExternalToolRunner`: timeout, hủy được, không dùng shell, tham số qua `ArgumentList`; stdin đóng ngay; stdout/stderr có giới hạn dung lượng; hết giờ hoặc hủy thì kill cả cây tiến trình; mặc định không log tham số (có thể chứa mật khẩu). Ngoại lệ duy nhất: mở thư mục bằng Explorer (`IShellLauncher`) là thao tác giao diện "bắn rồi quên", vẫn không dùng shell.

### Hạ tầng ứng dụng (Phase 1)

- Dữ liệu người dùng: `%LOCALAPPDATA%\PdfPro\` gồm `settings.json`, `logs\`, `sessions\`, `cache\`, `workflows\` (lớp `AppPaths`).
- `settings.json` có `schemaVersion`; file hỏng hoặc của phiên bản mới hơn được đổi tên thành `settings.json.corrupt-…`/`.newer-…` rồi dùng mặc định. Ghi bằng `AtomicFile` (file tạm → flush → `File.Replace`).
- Log Serilog: `pdfpro-yyyyMMdd.log`, giữ 14 ngày, tối đa 20 MB/file, chế độ shared.
- Mọi exception chưa xử lý được log kèm **mã sự cố** 8 ký tự và hiện hộp thoại thân thiện; lỗi trên UI thread không làm tắt app.
- Chuỗi giao diện: `src/PdfPro.App/Resources/Strings.resx` (vi, mặc định) + `Strings.en.resx`. XAML dùng `{loc:Tr Key}`, C# dùng lớp sinh tự động `Strings`. Đổi ngôn ngữ áp dụng sau khi khởi động lại. Test bắt buộc hai bảng có cùng key, cùng placeholder, và mọi key dùng trong XAML đều tồn tại.
- WPF biên dịch assembly tạm (markup pass 1) trước bước sinh resource, nên `PdfPro.App.csproj` có target `PdfProGenerateStringsBeforeMarkupCompile` sinh lớp `Strings` trước.

### Engine PDFium & trình xem (Phase 2)

**PDFium (`PdfPro.Pdfium`)**
- `PdfiumGate`: một luồng riêng chạy mọi lời gọi PDFium, hàng đợi theo ưu tiên `Release` (giải phóng handle) < `High` (trang đang thấy, đo hình học trang) < `Normal` (trang đệm, bookmark, metadata, in) < `Low` (thumbnail); cùng mức thì FIFO. Hủy chỉ bỏ được việc chưa bắt đầu.
- Mọi handle (document, page, text page, bitmap) là `SafeHandle`; lệnh đóng native luôn chạy trên luồng PDFium. Tài liệu giữ tối đa 6 trang đã load (LRU).
- Mở file qua `FPDF_LoadCustomDocument` với `FileStream` của .NET, mở bằng `FileShare.Read | Delete`: tệp không bị ghi đè khi đang xem (nên dấu vân tay nội dung luôn khớp). PDFiumCore khai báo `unsigned long` là 64-bit nhưng trên Windows là 32-bit → phải che nửa trên; tệp tối đa 4 GB.
- Lỗi mở tệp đọc `FPDF_GetLastError` → `PdfDocumentException` có loại: không tìm thấy, không có quyền, đang bị ghi, quá lớn, lỗi đọc, hỏng, cần mật khẩu, sai mật khẩu, kiểu bảo mật không hỗ trợ.
- Render ra BGRx (bố cục của WPF `Bgr32`) trên nền trắng; một lần render tối đa 100 triệu pixel. Cờ: annotation, LCD text, grayscale, printing; xoay thêm theo phần tư vòng (`ExtraQuarterTurns`) cho page plan và in.
- Bookmark đọc cả đích trực tiếp lẫn action GoTo, chống vòng lặp, tối đa 50.000 mục / 64 cấp. `SecurityHandlerRevision = -1` nghĩa là không mã hóa.

**Trình xem (`PdfPro.App`)**
- `DocumentViewPanel` (panel tự viết, `IScrollInfo`) đặt trang theo `DocumentLayout` (Core); chỉ trang đang thấy + 1 trang đệm mỗi phía có element. Chế độ 1 trang / 2 trang đối diện / 2 trang có bìa; zoom 10–800 %, Ctrl + lăn chuột giữ điểm dưới con trỏ.
- `PageRenderScheduler`: trang thấy được render ưu tiên `High`, trang đệm `Normal`; trong khi zoom/cuộn giữ ảnh cũ phóng tạm, render nét sau 150 ms (zoom) / 120 ms (cuộn) không đổi; trang rất lớn (trên 16 triệu pixel) có ảnh nền giới hạn + ô chi tiết nét cho phần đang thấy; trang trắng tạm hiện thumbnail (mờ) nếu có trong cache.
- `RenderCache` (LRU theo byte, dùng chung mọi tab, khóa = tài liệu, trang, scale, vùng). **Ảnh trong cache không bao giờ được vẽ trực tiếp**: WPF (render phần cứng) giữ thêm một bản texture cho mỗi bitmap đã vẽ chừng nào bitmap còn sống, độn theo kích thước texture GPU muốn, nên trang hiển thị một view nhẹ `DisplayImage` (CroppedBitmap đóng băng, không sao chép điểm ảnh); bản texture đi theo view và mất khi trang rời màn hình. Kết quả đo trên `big-1000.pdf` (ngân sách 512 MB): cuộn hết xuống rồi lên, RAM ổn định ~730 MB; mở 5 bản cùng lúc và chuyển tab, ổn định ~810 MB.
- Đo được: trang đầu của `big-1000.pdf` hiện sau ~1,45 s tính từ lúc khởi động tiến trình.

**Sidebar**
- Thumbnail: trang được co vào khung 160 × 226 DIP, số pixel theo DPI màn hình. Danh sách ảo hóa + tái chế; chỉ ô đang hiện giữ ảnh (behavior `Realization`, phải nhớ item đã realize vì khi tái chế container WPF đổi DataContext sang `{DisconnectedItem}` mà **không** phát `DataContextChanged`). Chọn theo trang hiện tại; click/Enter/mũi tên → nhảy trang.
- Thứ tự lấy thumbnail: `RenderCache` → cache đĩa → render `Low` rồi ghi đĩa (nền). Cache đĩa: `%LOCALAPPDATA%\PdfPro\cache\thumbnails\{dấu vân tay}\{trang}_{rộng px}.png`; dấu vân tay = SHA-256 của (độ dài tệp + 1 MB đầu + 1 MB cuối), 32 ký tự hex; ghi nguyên tử. Thời điểm dùng gần nhất = LastWriteTime của thư mục (cập nhật ở lần đọc đầu mỗi phiên). Dọn 30 s sau khi khởi động: bỏ thư mục không dùng quá 30 ngày, rồi bỏ cũ nhất cho tới dưới 500 MB. **Tài liệu có mã hóa không bao giờ được ghi/đọc cache đĩa** (ảnh trang không được nằm trên đĩa khi thiếu mật khẩu). PNG đọc từ đĩa được chép sang bitmap không gắn luồng (bộ giải mã PNG gắn với luồng nền đã giải mã).
- Bookmark: đọc nền khi mở tab; tiêu đề chuẩn hóa NFC, bỏ ký tự điều khiển (`TextNormalizer`); một gốc duy nhất thì tự mở; click/Enter → trang đích, cuộn tới tọa độ `/XYZ` qua `DestinationPlacement` (dùng `PageCoordinateMapper`), giữ mức zoom hiện tại.

**Thuộc tính tài liệu (Ctrl+D)**: Info dictionary (đọc nền), tên/thư mục/kích thước tệp, phiên bản PDF, số trang, khổ giấy (`PaperSizes`: ISO A0–A6, B4–B5, Letter, Legal, Tabloid, Executive, sai số 3 pt; dọc/ngang; "nhiều khổ giấy"), mã hóa + quyền in/sao chép, chữ ký số, form (AcroForm/XFA), JavaScript.

**Một phiên bản (single-instance)**: lần mở đầu tiên của phiên đăng nhập giữ named mutex `Local\PdfPro-{băm người dùng}-{session}` và lắng nghe named pipe cùng tên (`PipeOptions.CurrentUserOnly` ở cả hai đầu). Lần mở sau gửi đường dẫn tuyệt đối (tính theo thư mục làm việc của nó) — thông điệp: 1 byte phiên bản + độ dài 4 byte + UTF-8 ngăn bởi NUL, tối đa 1 MB — gọi `AllowSetForegroundWindow` cho tiến trình chính rồi thoát; cửa sổ chính được đưa lên trước và mở các tệp (danh sách rỗng = chỉ đưa lên trước). Thông điệp hỏng hoặc client treo quá 5 s bị bỏ. Không được trả lời trong 5 s thì lần mở này tự mở cửa sổ riêng. Khởi động lại truyền `--wait-for-pid` để phiên mới chờ phiên cũ thoát hẳn.

**In (Ctrl+P)**
- Hộp thoại in của Windows (trên Windows 11 có thể là hộp thoại kiểu mới chạy ở tiến trình riêng): máy in, số bản, hướng giấy, tất cả / trang hiện tại / từ–đến. Tài liệu cấm in (bit quyền 3) bị từ chối kèm giải thích.
- Từng trang render bằng PDFium với cờ `Printing | Annotations` ở độ phân giải máy in (tối đa 300 DPI), co vừa vùng in được, căn giữa, tự xoay khi hướng trang khác hướng giấy (`PrintLayout`, Core). Trang render lỗi in trắng và được báo.
- Đường in XPS chạy trên **một luồng STA riêng có dispatcher riêng** (`XpsPrintRunner`), không chặn UI. `PrintQueue` gắn với luồng đã mở nó, nên trên luồng in mở lại queue theo tên (`PrintServer(host).GetPrintQueue(name)`) và dựng lại ticket từ XML. Kết quả chỉ báo về sau khi đã giải phóng queue/gói XPS. Hủy qua `XpsDocumentWriter.CancelAsync` để spooler bỏ job sạch sẽ; đóng tab đang in thì hủy và chờ job dừng rồi mới đóng tài liệu.
- Thanh trạng thái hiện tác vụ nền (chữ, thanh tiến độ, nút Hủy); mỗi lúc một tác vụ.
- Lưu ý kiểm thử: in tới "Microsoft Print to PDF" chạy được từ app WPF (kể cả luồng nền) nhưng thất bại (E_FAIL) từ tiến trình console như test host; test tự động dùng gói XPS trên đĩa thay cho máy in.

**Cài đặt bổ sung (`settings.json`)**: `renderCacheMegabytes` (128–4096, mặc định 512) — áp dụng ngay; thẻ "Bộ nhớ đệm" hiện dung lượng thumbnail trên đĩa và nút xóa cả cache bộ nhớ lẫn đĩa.

**Quyền riêng tư trong log**: log không bao giờ chứa đường dẫn, tên tệp, mật khẩu hay nội dung tài liệu — chỉ số trang, số lượng, phiên bản PDF, cờ mã hóa, mã lỗi. Danh sách tệp gần đây chỉ nằm trong `settings.json`.

### Phiên tài liệu, PagePlan & sắp xếp trang (Phase 3)

**Thư mục phiên (`SessionStore`, Infrastructure)**

```text
sessions\{id}\session.lock        mở độc quyền khi phiên đang sống; tiến trình chết thì khóa tự nhả
              manifest.json       ghi nguyên tử sau mỗi thay đổi (ghi nền, gộp các lần ghi dồn dập)
              sources\s0.pdf      bản sao file đã mở; s1.pdf, s2.pdf… là các tệp được chèn trang
              revisions\r0001.pdf kết quả các thao tác nội dung (Phase 3: Đổi khổ giấy)
              work\               tệp tạm (plan đã vật chất hóa, bản sửa của nguồn hỏng)
```

- Mở tệp = sao chép vào `sources\s0.pdf` rồi mở bản sao; file gốc không bị giữ mở và chỉ được ghi khi bấm Lưu.
- Tên tệp revision lấy theo số của id nguồn (`r2` → `r0002.pdf`), không theo số revision: hoàn tác một thao tác nội dung rồi làm thao tác khác không ghi đè tệp mà bước làm lại (redo) còn cần.
- Nguồn không bao giờ bị gỡ khỏi phiên (tệp chèn rồi hoàn tác vẫn ở đó để làm lại và để khôi phục sau crash).
- Biến môi trường `PDFPRO_DATA_DIR` đổi thư mục dữ liệu (test UI, bản portable). Tên kênh "một phiên bản" băm cả thư mục dữ liệu, nên một lần chạy với thư mục riêng không bao giờ gửi tệp sang app người dùng đang mở.

**Manifest và khôi phục sau crash**
- `manifest.json`: đường dẫn gốc + `FileStamp` (kích thước, thời điểm sửa), danh sách nguồn, state hiện tại (plan, nguồn cơ sở, số revision), state gốc của lịch sử, các bước undo/redo (`HistoryRecord`: id thao tác + tham số JSON + khóa các trang bước đó tạo + nguồn revision nó tạo), điểm lưu, cờ "chưa lưu cưỡng bức", `pendingSave`. Không chứa mật khẩu; log chỉ ghi id phiên.
- Khi khởi động, phiên "mồ côi" (lấy được khóa): không có thay đổi → xóa im lặng; còn `pendingSave` → sửa trước (file đích mất thì trả bản backup về, xóa tệp tạm); có thay đổi → hộp thoại 3 nút "Khôi phục mục đã chọn" (mục bỏ chọn bị xóa) / "Bỏ tất cả" (có xác nhận) / "Để sau".
- Khôi phục = phát lại các bước từ state gốc với đúng các khóa trang đã ghi (`ReplayPageKeys`), đặt lại điểm lưu rồi hoàn tác các bước redo, nên được lại cả tài liệu lẫn lịch sử Hoàn tác/Làm lại. Bước của thao tác nội dung được dựng lại thành plan đồng nhất trên revision đã ghi. Không phát lại khớp (thao tác lạ, quy tắc đổi) → khôi phục state hiện tại, mất lịch sử, đánh dấu chưa lưu — không bao giờ mất tài liệu.

**Core**
- `PageRef(SourceId, SourcePageIndex, RotationDelta, CropBoxOverride?, PageKey)`; trang trắng có `BlankSize` và nguồn `"blank"`. `PageCrop` là vùng giữ lại, đo bằng point từ góc trên-trái của trang **như đang hiển thị với /Rotate của chính nó** (chưa tính xoay của plan); cắt thêm lần nữa thì lề tính trên phần đang hiển thị.
- `PagePlan` bất biến: `Move`, `Rotate`, `Delete`, `Duplicate`, `InsertBlank`, `InsertFromSource`, `Crop` trả plan mới (trả chính nó khi không đổi gì → không có bước lịch sử). Vùng chọn, ảnh cache và trạng thái view theo `PageKey`, nên đi theo trang khi sắp xếp và khi hoàn tác.
- `PageRanges`: "1-5, 8, 10-", "-3", "5-3", "odd"/"lẻ", "even"/"chẵn", "all"/"tất cả"; chuẩn hóa NFC.
- `DocumentSession` (chỉ dùng trên UI thread): `SessionState(Plan, BaseSourceId, Revision)`; `PlanCommand` (sửa plan) và `RevisionCommand` (thao tác nội dung: revision +1, plan = mọi trang của revision) chỉ đổi qua lại hai state bất biến nên hoàn tác tức thì ở mọi kích thước.
- `OperationKind`: `PlanOnly`, `Content`, `Export`. `IPlanOperation.ApplyToPlan` (thuần, không I/O) và `IContentOperation.ApplyToFileAsync` (đọc tệp đầy đủ, ghi ra stream).

**Operations (PDFsharp 6.2.4)**
- `organize.rotate | move | delete | duplicate | insertBlank | insertPages | crop` (PlanOnly), `organize.extract` (Export), `organize.pageSize` (Content). Chạy trên tệp (CLI, lô) thì tệp vào thành nguồn s0, s1… và kết quả ghi vào thư mục ra, không bao giờ đè tệp vào.
- `PdfSharpPlanMaterializer`: mở nguồn cơ sở ở chế độ Modify và sắp lại cây trang (`MovePage`), nên giữ metadata, bookmark, đích có tên, form, link nội bộ (chúng trỏ tới đối tượng trang). Trang bị xóa: cắt mọi tham chiếu; link/bookmark/open action trỏ tới nó bị bỏ, bookmark có con giữ lại làm tiêu đề không đích. Nhân bản: dùng chung nội dung, annotation chép riêng (ô form không chép — Phase 7). Trang từ tệp khác: gỡ tham chiếu trang khỏi link trước khi import, nối lại link nếu trang đích cũng được chèn. `/PageLabels` bị bỏ khi thứ tự hoặc số trang đổi. Crop → CropBox qua `PageCoordinateMapper`. Nguồn PDFsharp không đọc được → PDFium ghi bản sạch (`SaveAsCopy`) rồi thử lại.
- Đổi khổ giấy (`PageResizer`): MediaBox mới (các box khác bị bỏ), nội dung cũ bọc trong `q s 0 0 s e f cm <vùng đang hiển thị> re W n … Q` ở một mảng `/Contents` riêng của trang (stream có thể dùng chung không bị sửa), nên chữ vẫn chọn/tìm được. Hai cách đặt: co giãn cho vừa (giữ tỉ lệ, căn giữa) hoặc giữ kích thước, căn giữa (trang nhỏ hơn thì bị cắt mép). "Theo hướng từng trang" xoay khổ mới cho khớp trang ngang/dọc. Annotation (`/Rect`, `/QuadPoints`, `/InkList`, `/Vertices`, `/L`, `/CL`, `/RD`) và đích (`/XYZ`, `/FitH`, `/FitBH`, `/FitV`, `/FitBV`, `/FitR` của link, bookmark, đích có tên, open action) đi theo phép biến đổi.

**Lưu an toàn (Ctrl+S / Ctrl+Shift+S)**
1. Cảnh báo trước: tệp có chữ ký số, hoặc tệp đã bị chương trình khác sửa từ lúc mở (so `FileStamp`).
2. Manifest ghi `pendingSave` (đích, tệp tạm, backup) **trước khi** ghi gì ra đĩa.
3. Vật chất hóa plan vào `.{tên}.{guid}.tmp` cạnh tệp đích → flush → PDFium mở lại kiểm tra số trang → `File.Replace` kèm backup tạm → xóa backup → xóa `pendingSave`.
4. Trong lúc lưu tài liệu không sửa được, để điểm lưu khớp đúng nội dung đã ghi. Lỗi được phân loại: đang bị chương trình khác mở (đề xuất Lưu thành), không có quyền ghi, đầy đĩa, không đọc được nguồn, tệp vừa ghi không mở lại được; trong mọi trường hợp tệp gốc nguyên vẹn và không sót tệp tạm.
5. Tài liệu có mật khẩu chỉ xem và in (lưu lại phải mã hóa lại bằng mật khẩu chủ — Phase 8).
- Đóng thẻ, đóng cửa sổ, thoát, khởi động lại: hỏi Lưu / Không lưu / Hủy lần lượt từng tài liệu. Windows tắt máy: giữ phiên để khôi phục.

**App**
- `VirtualDocument`/`VirtualPage`: plan → danh sách trang (tài liệu nguồn, trang nguồn, xoay, crop, kích thước hiển thị, tùy chọn render). Viewer, sidebar, lưới sắp xếp, in và Thuộc tính đều đọc nó. Khóa cache render gồm cả xoay và crop, nên di chuyển trang không mất ảnh và xoay chỉ render lại trang bị xoay.
- Lệnh trang (tab Trang, menu chuột phải, phím tắt) áp cho **trang hiện tại** ở chế độ Xem và cho **các trang đang chọn** ở chế độ Sắp xếp. Ctrl+R / Ctrl+Shift+R xoay, Delete xóa (trong lưới), Ctrl+A chọn hết, Ctrl+Z / Ctrl+Y.
- **Lưới "Sắp xếp trang"** thay viewer và ẩn sidebar. `VirtualizingTilePanel` (tự viết, `IScrollInfo`, tái chế container; chỉ hàng đang thấy + 1 hàng mỗi phía có element; đổi cỡ ô giữ ô đầu đang thấy). Hai bẫy WPF: đọc `InternalChildren` trước khi lấy `ItemContainerGenerator` (nếu không generator là null), và container tái chế được `GenerateNext` trả về với `isNewlyRealized = false` dù không còn là con của panel. `PageTileListBox`: chọn mở rộng, kéo khung chọn trên vùng trống (Ctrl để cộng thêm, tự cuộn sát mép), đúp chuột/Enter mở trang trong viewer, đồng bộ vùng chọn với view model. Cỡ ô 120–360 DIP bước 40 (thanh trượt, Ctrl + lăn chuột); ảnh ô có mục cache bộ nhớ riêng theo cỡ, không ghi cache đĩa.
- Quy tắc vùng chọn sau mỗi thay đổi: các trang mới xuất hiện (bản nhân bản, trang chèn, trang được hoàn tác xóa); không có thì các trang đang chọn còn lại; không còn thì trang đã thế chỗ trang chọn đầu tiên; mọi trang đều mới (thao tác nội dung) thì giữ nguyên vị trí. Xoay/cắt lề giữ nguyên danh sách ô (không dựng lại).
- Kéo-thả bằng gong-wpf-dragdrop 4.0.0: kéo các trang đang chọn để di chuyển (vạch chèn, tự cuộn); thả tệp PDF từ Explorer vào lưới để chèn tại chỗ thả.
- Hộp thoại: Chèn trang trắng (giống trang kề / A4 / A3 / Letter / Legal, dọc/ngang, 1–1000 trang, trước/sau trang đầu/cuối/trang N), Chèn từ tệp (cả tệp hoặc khoảng trang; tệp được chép vào phiên thành nguồn sN; tệp có mật khẩu bị từ chối ở P3), Cắt lề (mm, bốn lề bằng nhau, bỏ cắt lề, xem trước trang đầu được chọn với phần bị cắt tô mờ), Đổi khổ giấy (A3/A4/A5/Letter/Legal/tùy chỉnh, hướng, cách đặt nội dung). Cắt lề và Đổi khổ áp cho trang chọn / tất cả / lẻ / chẵn / khoảng trang.
- Trích trang ra tệp mới và Đổi khổ giấy chạy nền với tiến độ và nút Hủy ở thanh trạng thái (mỗi lúc một tác vụ); đóng tài liệu chờ tác vụ đang đọc tệp của nó. Đổi khổ: vật chất hóa plan vào `work\`, thao tác ghi `revisions\rNNNN.pdf.tmp` → flush → đổi tên → mở và kiểm số trang → ghi `RevisionCommand`; lỗi hoặc hủy thì xóa tệp và tài liệu giữ nguyên.

**Số đo nghiệm thu (máy phát triển, bản Release)**
- Xoay / di chuyển / nhân bản / xóa 200 trang của tài liệu 1.000 trang, từ lệnh tới khi mọi view model (viewer, thumbnail, lưới) cập nhật: trung vị 4–5 ms (`PageOperationTimingTests`, mục tiêu < 50 ms). Đo qua UI Automation từ ngoài (gồm cả độ trễ UIA): 450–560 ms.
- Lưới 1.000 trang: mở chế độ Sắp xếp ~0,45 s; cuộn hết lưới, bộ nhớ riêng ổn định ~170 MB.
- Đổi khổ A5 cho 3 trang của `basic-vi.pdf`: ~0,6 s.
- 30 thao tác rồi hoàn tác hết: tài liệu về đúng trạng thái đã mở (không còn dấu "*"). SHA-256 tệp gốc không đổi cho tới khi Lưu. Tắt cứng app ở 5 thời điểm trong lúc lưu `big-1000.pdf`: tệp luôn mở được (bản cũ hoặc mới), tệp tạm được dọn ở lần mở sau. Tắt cứng khi còn thay đổi chưa lưu (kể cả revision): khôi phục đủ thay đổi và lịch sử. Tệp đã lưu mở đúng trong Microsoft Edge.

**Kiểm thử giao diện**: `tests/PdfPro.App.UiTests` (FlaUI 5.0, MIT) chạy app thật qua UI Automation và thông điệp gửi tới cửa sổ (không dùng chuột/bàn phím thật), mỗi test một thư mục dữ liệu riêng qua `PDFPRO_DATA_DIR`. Chỉ chạy tay: đặt `PDFPRO_UI_TESTS=1` rồi `dotnet test --project tests/PdfPro.App.UiTests -c Release`; không đặt biến thì mọi test được bỏ qua (CI). Lưu ý: UI Automation đặt cửa sổ có chủ (hộp thoại) dưới cửa sổ chủ chứ không dưới desktop, và hộp thoại của WPF-UI lấy nội dung làm tên — tìm hộp thoại bằng `EnumWindows` + tiêu đề Win32. Kéo-thả và kéo khung chọn không tự động hóa được nếu không dùng chuột thật (WPF đọc vị trí con trỏ thật), nên được kiểm bằng unit test hình học và kiểm tay.

### Gộp, tách & bố cục trang (Phase 4)

**Dấu trang đầu (tách một tập scan gồm nhiều văn bản)**
- Người dùng đánh dấu trang đầu của từng văn bản; khi tách, mỗi trang có dấu mở đầu một tệp mới, các trang sau nó tới trước dấu kế tiếp thuộc tệp đó. Trang 1 luôn mở đầu tệp thứ nhất (không cần dấu).
- `SplitMark(FileName?)` nằm trên `PageRef`, nên đi theo trang khi sắp xếp, xoay, xóa; bản nhân bản và trang chèn không mang dấu (`WithKey` bỏ dấu). `PagePlan.MarkSplit / WithSplitMarks / GetSplitStarts`.
- Đặt/bỏ dấu là bước Hoàn tác/Làm lại được nhưng **trung tính với nội dung** (`PlanCommand(changesContent: false)`, `IPlanOperation.ChangesContent`): không làm tài liệu "chưa lưu", điểm lưu so theo nội dung (`PagePlan.HasSameContent`) chứ không theo vị trí lịch sử. Dấu không ghi vào PDF.
- Ghi nhớ theo tệp ngoài PDF: `data\split-marks\{32 hex của SHA-256 đường dẫn đầy đủ}.json` (không chứa đường dẫn) gồm `FileStamp`, số trang và các dấu; tệp bị sửa nơi khác thì dấu bị bỏ qua; giữ 500 tài liệu mới nhất. Manifest phiên cũng chứa dấu (`PageDto.Split`) để khôi phục sau crash.
- Giao diện: Ctrl+M (trang hiện tại ở chế độ Xem, các trang chọn ở chế độ Sắp xếp), menu chuột phải, tab Trang; huy hiệu "Tệp n" trên ô lưới và thumbnail, dải màu xen kẽ theo tệp trong lưới, thanh trạng thái hiện tệp của trang hiện tại, dòng tóm tắt "n dấu → m tệp" và nút "Tách theo dấu…".

**Tách (`organize.split`, Export) — 7 chế độ**: theo khoảng ("1-3, 4-10, 11-"), mỗi N trang, theo bookmark cấp k, theo dung lượng tối đa, theo trang trắng phân cách, theo văn bản (regex, NFC, tùy chọn phân biệt hoa thường), theo trang đánh dấu.
- `SplitPlanner` (Core, thuần) chia plan thành `SplitSegment`; `SplitAnalysis` chỉ đọc thứ chế độ cần (bookmark, text từng trang, ảnh trang) và báo tiến độ; `SplitWriter` ghi mọi tệp vào `.{guid}.pdfpro-split.tmp` trong thư mục đích, xong hết mới đổi tên (ghi đè có backup tạm), lỗi hoặc hủy thì xóa sạch và trả lại tệp cũ — **được tất cả hoặc không gì**.
- `BlankPageDetector`: tỉ lệ điểm tối trên ảnh xám, bỏ 5% lề, ngưỡng mặc định 0,1% (tờ phân cách của `scan-vi.pdf` ~0,02%, trang chữ thưa nhất ~0,36%); trang phân cách bị loại khỏi kết quả; chế độ khác có tùy chọn "bỏ trang trắng".
- Theo dung lượng: ước lượng theo trang (`PageSizeEstimator`) rồi kiểm dung lượng thật sau khi ghi, tệp vượt thì chia lại; một trang lớn hơn giới hạn thành tệp riêng kèm cảnh báo.
- Tên tệp: mẫu `{name}`, `{index}`/`{index:000}`, `{bookmark}`, `{match}`, `{first}`, `{last}`; tên theo bookmark/văn bản qua `FileNames.Sanitize` (NFC, `/ \ |` → `-`, bỏ `: * ? " < >`, tránh tên thiết bị, ≤ 120 ký tự); trùng tên → "(2)", hoặc chọn ghi đè. Từng dòng xem trước sửa được tên; tên đặt cho dấu được nhớ cùng dấu.
- Hộp thoại xem trước: danh sách tệp (tên, khoảng trang, số trang), tóm tắt, cảnh báo; thư mục đích mặc định `{tên tệp} - tách` cạnh tệp nguồn; "Mở thư mục khi xong"; "Chuyển thành dấu" biến kết quả của chế độ khác thành dấu để chỉnh tay. Tài liệu đang sửa được tách từ bản vật chất hóa của plan; tệp gốc không bị ghi. Kết quả không bao giờ được mã hóa (P8); tài liệu có mật khẩu: tách và bố cục bị tắt tới P8.

**Gộp (`organize.merge`)**: danh sách tệp (thêm, kéo sắp xếp, thả từ Explorer, sắp theo tên tự nhiên, xóa), mỗi tệp chọn khoảng trang và tên hiển thị trong bookmark/mục lục; tùy chọn bookmark mang tên tệp, giữ bookmark gốc (lồng dưới bookmark tên tệp), trang mục lục có liên kết ở đầu, mỗi tệp bắt đầu ở trang lẻ (chèn trang trắng), khổ giấy (giữ nguyên / theo trang đầu / A4, giữ hướng, co cho vừa).
- Các tệp thành nguồn s0…sN của một plan, vật chất hóa bằng `PdfSharpPlanMaterializer` (giữ link nội bộ, chú thích); bookmark dựng lại từ bookmark PDFium của từng nguồn theo vị trí trang mới; mục lục vẽ bằng Noto Sans (OFL, nhúng subset, đóng gói trong `PdfPro.Operations.dll`) với link qua `PageCoordinateMapper`. Tệp kết quả chỉ mang Title (= tên tệp).
- Tệp có mật khẩu hỏi ngay khi thêm (mật khẩu chỉ trong bộ nhớ: `InputPasswords`, không vào log/manifest); chỉ gộp được khi có quyền "lắp ráp tài liệu" hoặc nhập mật khẩu chủ; tệp hỏng bị từ chối kèm lý do. Kết quả không được trùng một tệp nguồn; ghi qua tệp tạm như Lưu, mở trong thẻ mới. Mở từ Trang chủ hoặc tab Trang, không cần tài liệu đang mở; tài liệu đang mở (kể cả đang sửa, qua bản sao làm việc) được đưa vào danh sách sẵn.

**Bố cục**
- `organize.interleave` (PlanOnly, hoàn tác được): trộn mặt trước + mặt sau khi scan 2 mặt bằng máy 1 mặt — mặt sau là nửa sau của chính tài liệu (chọn trang bắt đầu) hoặc một tệp khác (thành nguồn mới); tùy chọn mặt sau đảo ngược; cảnh báo khi hai nửa lệch số trang; xem trước thứ tự.
- `layout.nup` (2/4/6/8/9/16 trang mỗi tờ, thứ tự Z hoặc N, lề, khoảng cách, viền, khổ và hướng tờ), `layout.booklet` (bội số 4, tự thêm trang trắng; đóng gáy trái/phải; lề, khoảng trống hai bên nếp gập; in 2 mặt lật cạnh ngắn), `layout.tile` (chia trang lớn thành nhiều tờ theo tỉ lệ % hoặc số cột × hàng, vùng chồng mép, dấu cắt, nhãn ô A1, A2…).
- Ba thao tác bố cục ghi tệp mới (`ImpositionOperation`): trang nguồn thành form XObject riêng (`PageForms`, BBox = vùng hiển thị, ma trận theo /Rotate; `XPdfForm` của PDFsharp bỏ qua CropBox nên không dùng), vẽ lên tờ bằng `SheetPainter`. Chú thích và liên kết không được giữ trên tờ in (hộp thoại có ghi chú). Hộp thoại xem trước tờ đầu tiên và số tờ (sổ gập: "n mặt in trên m tờ giấy"); kết quả mở trong thẻ mới.

**Số đo nghiệm thu (máy phát triển, bản Release)**
- Tách `scan-vi.pdf` (18 trang ảnh) theo 4 dấu, bỏ trang trắng → 5 tệp 3/2/2/3/4 trang: 0,97 s từ lúc bấm Tách tới thông báo xong (đo qua UI Automation).
- Gộp 20 tệp (168 trang) kèm bookmark và mục lục, tới khi kết quả mở trong thẻ: 1,37 s; đủ 20 bookmark tên tệp + toàn bộ bookmark gốc đúng trang, 20 link mục lục đúng trang đầu từng tệp.
- Tách `big-1000.pdf` thành 100 tệp 10 trang: 9,7 s (~95 ms/tệp, có tiến độ, hủy được). Tách theo bookmark `vanban-vi.pdf`: 5 tệp tên đúng dấu tiếng Việt ("Quyết định số 12-QĐ-UBND.pdf", "Hợp đồng 128-2026-HĐ-MT.pdf"…).
- Sổ gập 10 trang → 12 trang (6 mặt A4 ngang trên 3 tờ), thứ tự in kiểm bằng golden test; in thử gập đôi là bước kiểm tay.
- Kịch bản FlaUI: `Phase4AcceptanceTests` (tách theo dấu + dấu được nhớ khi mở lại, gộp 20 tệp, tách theo bookmark tiếng Việt, sổ gập từ tài liệu đang sửa).

### Chèn & đóng dấu (Phase 5)

**Đối tượng overlay (Core)**
- Đối tượng tạm (`OverlayItems.cs`, record bất biến, serialize JSON = tham số của `edit.overlay`): hộp chữ, ảnh (PNG giữ alpha, JPEG nhúng nguyên byte, cờ chữ ký tay), chữ nhật, elip, che trắng (tô màu, không viền — **chỉ che về hình**, không xóa nội dung bên dưới; xóa thật là redaction ở P8), đường thẳng/mũi tên. `PageIndex` tính trên tài liệu như đang hiển thị.
- `OverlayBox`: **tâm** đặt trong không gian PDF của trang, rộng/cao bằng point, góc xoay theo chiều kim đồng hồ so với trang như đang hiển thị; mọi chuyển đổi qua `PageCoordinateMapper`, nên zoom hay /Rotate không làm lệch. Không gian đó là trang **sau khi ghi** (`MaterializedGeometry`: hộp nguồn + /Rotate + crop và xoay của plan), dùng chung cho trình chỉnh sửa và materializer.
- `RotatedBox`: chọn, 8 nút kéo, nút xoay (Shift: bước 15°), hit-test; `OverlaySnapping` hít vào mép/tâm trang và đối tượng khác kèm đường gióng. `OverlayLimits`: cạnh ≤ 14.400 pt, cỡ chữ 1–400, giãn dòng 0,5–5, viền ≤ 72 pt, chữ ≤ 20.000 ký tự.
- `TextLayout`: một bộ ngắt dòng cho cả màn hình lẫn PDF, từ số đo của chính mặt chữ nhúng (NFC, xuống dòng theo từ, từ quá dài bị bẻ giữa các phần tử văn bản, căn trái/giữa/phải/đều, giãn dòng) — chữ trên màn hình và trong PDF xuống dòng y hệt.

**Phông chữ (`FontLibrary`, Operations)**
- Đóng gói Noto Sans, Noto Serif, Be Vietnam Pro đủ 4 kiểu (SIL OFL 1.1) trong `PdfPro.Operations.dll`; thêm phông cài trên máy nếu viết đủ mọi chữ tiếng Việt, có đường viền TrueType và giấy phép cho nhúng subset (bỏ `.ttc` vì PDFsharp 6.2 không nhúng được). Nghiêng thiếu thì giả bằng cách xiên; **đậm không bao giờ giả** (không có mặt đậm thì không cho chọn đậm).
- Cùng một mặt chữ dùng để đo, vẽ màn hình (`ScreenFonts` ghi phông đóng gói ra cache cho WPF) và nhúng PDF (subset + ToUnicode). Phông cài trên máy được đọc nền lúc khởi động.
- Kiểm thử: quốc hiệu và mọi chữ cái tiếng Việt viết bằng 12 mặt chữ đóng gói, đọc lại đúng bằng PdfPig và PDFium.

**Ghi vào PDF (`edit.overlay`, các `stamp.*`)**
- `StampForms` vẽ bằng PDFsharp lên trang nháp rồi biến thành form XObject (chỉ API công khai, có lề để nét không bị cắt). `PageStamper` đặt form bằng ma trận từ `PageCoordinateMapper`; nội dung cũ bọc `q…Q` khi dấu nằm trên; mỗi trang được `/Resources` riêng (tài nguyên dùng chung hoặc thừa kế không bao giờ bị sửa); độ mờ qua ExtGState + nhóm trong suốt.
- Dấu gỡ được nằm trong optional content group ("Watermark", "Background", "Header/Footer"; mỗi tên một layer cho cả tài liệu) và stream của nó mang khóa `/PdfProStamp` với loại (`Watermark`, `Background`, `HeaderFooter`, `PageNumbers`, `Bates`) — `stamp.remove` nhận ra dấu nhờ khóa này.
- `DocumentContentOperation`: mở / kiểm / lưu chung cho thao tác nội dung; trong phiên chạy trên bản vật chất hóa của plan (tên GUID) → **`{filename}` luôn lấy từ tham số** (`FileName`), không từ tên tệp vào.

**Trình chỉnh sửa trang (App)**
- "Thêm chữ / ảnh / hình / chữ ký tay": đối tượng nằm trên `OverlayLayer` phía trên ảnh trang, giữ theo `PageKey` (trang có thể di chuyển, xoay trong lúc chờ). Vẽ đúng như sẽ ghi (chữ bằng GlyphRun với độ rộng ký tự của mặt nhúng, ảnh theo hướng EXIF).
- Click = đối tượng cỡ mặc định, kéo = theo khung; kéo di chuyển (hít, Alt tắt hít), 8 nút đổi cỡ (Shift hoặc ảnh: giữ tỉ lệ), nút xoay; mũi tên dịch 1 pt (Shift 10 pt); Delete; Ctrl+C/Ctrl+V được giữa các trang (mỗi lần dán lệch 10 pt), dán ảnh chép từ chương trình khác. Gõ chữ tại chỗ (đúp chuột để sửa), hộp tự cao theo dòng; hộp rỗng bị bỏ; thêm + gõ là một bước.
- Panel thuộc tính: phông, cỡ, đậm (chỉ khi có mặt đậm), nghiêng, màu (bảng + mã hex), căn lề, giãn dòng, viền, nền, màu/độ dày nét, đầu mũi tên, độ mờ; mỗi thay đổi là một bước và thành kiểu cho đối tượng mới.
- Trình chỉnh sửa có Hoàn tác/Làm lại riêng (Ctrl+Z hoàn tác sửa đổi của nó trước rồi mới tới tài liệu). "Áp dụng" ghi tất cả bằng **một** `edit.overlay` → revision mới → **một bước hoàn tác** của tài liệu. Đối tượng đang chờ được áp dụng trước khi Lưu, In, đóng thẻ, thoát, sang chế độ Sắp xếp; trong lúc còn đối tượng chờ, các thao tác nội dung khác bị tắt.
- Ảnh: JPEG chép nguyên byte vào phiên (không nén lại), định dạng khác thành PNG 8-bit đặt tên theo nội dung; đặt giữa phần đang thấy của trang, không quá nửa trang.

**Chữ ký tay (dạng ảnh)**
- Vẽ bằng chuột/bút (nét làm mượt, độ dày), gõ tên bằng Dancing Script hoặc Charm (OFL, chép vào `Fonts\` cạnh exe), hoặc nhập ảnh chụp/scan (`SignatureImages`: nền giấy thành trong suốt có mép mềm, cắt sát nét mực). Xuất PNG nền trong suốt 288 DPI, cắt sát mực; đặt như một ảnh chữ ký.
- Thư viện chữ ký: mỗi chữ ký một tệp, PNG **mã hóa DPAPI theo tài khoản Windows**, giữ 20 chữ ký mới nhất; mục không đọc được bị bỏ qua. Giao diện ghi rõ đây là chữ ký hình ảnh, **khác chữ ký số** (P8).

**Đóng dấu hàng loạt — hộp thoại có xem trước**
- `stamp.watermark` (chữ hoặc ảnh; 9 vị trí hoặc lát kín trang; xoay; độ mờ; trên/dưới nội dung; layer "Watermark"), `stamp.background` (màu hoặc ảnh kéo giãn/giữ tỉ lệ, dưới nội dung), `stamp.headerFooter` (6 vùng, `{page} {pages} {date[:định dạng]} {filename} {title}`, bỏ trang bìa và đánh số từ trang sau, số bắt đầu, kiểu 1/i/I), `stamp.pageNumbers` (mẫu như "Trang {page}/{pages}", từ trang k với số n, 6 vị trí), `stamp.remove` (gỡ các loại đã chọn cùng tài nguyên của chúng và layer không còn trang nào dùng; trang hiện lại y như trước; đối tượng của trình chỉnh sửa không bị đụng tới).
- Một cửa sổ chung (`StampWindow`), mỗi hộp thoại một template; "Áp dụng cho" trang đã chọn / tất cả / lẻ / chẵn / khoảng trang (mặc định: vùng chọn của chế độ Sắp xếp). **Xem trước = chạy đúng thao tác thật** trên bản 1 trang của trang hiện tại (xuất một lần vào `work\`), PDFium render, 250 ms sau lần sửa cuối; chữ phụ thuộc trang được viết sẵn (`TextsOn`/`TextOn`) rồi `StampTemplate.Escape`. Trang ngoài phạm vi áp dụng hiện nguyên trạng. Tệp xem trước bị xóa khi đóng hộp thoại.
- "Gỡ dấu" quét tài liệu **như đang hiển thị** (xuất plan ra `work\stamps-*.pdf`, đếm ở luồng nền, xóa bản tạm) và liệt kê từng loại kèm số trang; không có dấu nào thì chỉ báo.
- Mỗi lần áp dụng là một thao tác nội dung (revision mới, một bước hoàn tác), chạy nền ở thanh trạng thái như Đổi khổ giấy.

**Số Bates nhiều tệp (`stamp.bates`, Export)**
- Một số chạy cho mọi trang của nhiều tệp, nối tiếp từ tệp này sang tệp kia, không trống, không trùng (`BatesNumbering`); nhãn = tiền tố + số đệm 0 (1–15 chữ số; số lớn hơn thì dài thêm, có cảnh báo) + hậu tố (≤ 50 ký tự, không ký tự điều khiển).
- Hộp thoại như Gộp: danh sách tệp (thêm, thả từ Explorer, kéo đổi thứ tự, sắp theo tên tự nhiên, bỏ) với dải số của từng tệp; nhãn của trang đầu; vị trí (6), phông, cỡ, đậm, màu; thư mục ra (mặc định `Đã đánh số Bates` cạnh tệp đầu), ghi đè, mở thư mục khi xong. Tài liệu đang mở có sẵn trong danh sách; đang sửa thì đánh số **bản sao của cái đang hiển thị**, đặt đúng tên tài liệu trong `work\bates-{guid}\` (bản đánh số giữ tên tệp vào), xóa sau khi chạy.
- Ghi nền với tiến độ và nút Hủy; mọi bản sao qua tệp tạm, **được tất cả hoặc không gì**; tệp gốc không bao giờ bị ghi; "Ghi đè" vào thư mục chứa tệp gốc bị chặn. Tệp có mật khẩu bị từ chối kèm giải thích (tới P8).

**Nghiệm thu**
- Unit/golden: đối tượng đặt trên trang /Rotate 0/90/180/270 và CropBox lệch gốc render đúng chỗ (PDFium) và đọc lại đúng dòng bằng PdfPig; chữ xem trước của đầu/chân trang và số trang khớp chữ thật ghi ra trên mọi trang; Bates nối tiếp qua nhiều tệp, lỗi một tệp thì không ghi gì; gỡ dấu xong trang render y như trước.
- Kịch bản FlaUI `Phase5AcceptanceTests` (chạy tay): hình mờ "TÀI LIỆU MẬT" trên mọi trang `basic-vi.pdf` là một bước hoàn tác, tìm được bằng PdfPig, nằm trong layer "Watermark", gỡ lại thì chữ các trang y như ban đầu; Bates "HS-" cho 3 tệp chạy liền 1…n, tệp gốc giữ nguyên SHA-256.
- Kiểm tay: chữ có đủ dấu (ẳ, ỡ, ự, Ỷ…) bằng 3 phông đóng gói mở đúng trên máy không cài phông và trong Acrobat Reader / Chrome; đối tượng không lệch khi zoom 50–400 % và trên trang /Rotate 90.
- Số đo trên máy phát triển: chưa đo (ghi lại khi chạy `Phase5AcceptanceTests` trên Windows — thời gian được in ra output của test).

### Chọn chữ, tìm kiếm & chú thích (Phase 6)

**Lớp chữ (PDFium → Core `PageText`)**
- `PdfiumTextPage.GetLayer` đọc từng ký tự với hộp `FPDFText_GetLooseCharBox` (cao từ ascent tới descent của phông → ký tự cùng dòng cùng đỉnh/đáy), trong không gian PDF của trang nguồn = không gian của trang sau khi vật chất hóa (map bằng `MaterializedGeometry` như overlay). `TextLayerCache` (LRU 256 trang) dùng chung cho chọn chữ, tìm kiếm và đánh dấu.
- `PageText`: dòng, từ, ký tự tại điểm, caret khi kéo, hình chữ nhật theo dòng; chép chữ NFC, nối dòng, bỏ gạch nối cuối dòng khi là từ bị bẻ, giữ ngắt đoạn.
- Viewer: kéo = chọn, đúp = từ, ba lần = dòng (kéo tiếp theo từ/dòng), chọn xuyên trang, Ctrl+A cả tài liệu, Esc bỏ, Ctrl+C; con trỏ chữ I trên chữ. Vùng chọn gắn với chỉ số trang nên bị xóa khi các trang đổi (không xóa khi chỉ chú thích đổi: `PagePlan.HasSamePages`).

**Tìm kiếm (Core `TextSearch`, App `SearchViewModel`)**
- NFC = NFD; hoa/thường; dấu **Tự động** (truy vấn không dấu thì bỏ dấu, có dấu thì khớp dấu), **Bỏ qua**, **Khớp** — cả đ/Đ; toàn từ; regex (timeout 2 s); cụm từ khớp qua chỗ xuống dòng. Kết quả là khoảng ký tự kèm đoạn trích ngữ cảnh.
- Chạy nền qua `TextLayerCache`, kết quả đổ dần, Dừng được; kết quả đang chọn được chọn chữ và cuộn tới; F3 / Shift+F3 vòng quanh; tự tìm lại khi đổi tùy chọn hoặc các trang đổi. Trang không có lớp chữ được đếm → gợi ý OCR (P10). `TextHighlightLayer` vẽ kết quả (vàng, đang chọn cam) và vùng chọn.

**Mô hình chú thích (Core `Annotations/AnnotationItems.cs`)**
- Record bất biến, JSON đa hình: markup (tô sáng, gạch chân, gạch ngang, lượn sóng — `QuadPoints` từ hộp ký tự), ghi chú, hộp chữ (có đường chỉ), mực, chữ nhật/elip, đường/mũi tên, đa giác/đường gấp khúc, con dấu chữ/ảnh. Mỗi chú thích có màu, độ mờ, nét, tác giả (Cài đặt → "Tên tác giả chú thích", trống = tên Windows), ngày tạo/sửa, nội dung, **trả lời** và **trạng thái duyệt** (Chấp nhận/Từ chối/Hủy/Hoàn thành). `Validate`, `MoveBy`, hit-test (`AnnotationHitTest`).
- `AnnotationGroup`: Ghi chú (ghi chú, hộp chữ, dấu chèn, tệp đính kèm, âm thanh) · Đánh dấu chữ · Hình vẽ · Con dấu — dùng cho bộ lọc của panel và cho làm phẳng theo loại.

**Ghi vào PDF (`AnnotationWriter`, PDFsharp tầng dictionary)**
- PDFium 157 có API annotation nhưng **không ghi được** `/L`, `/Vertices`, `/IRT`; PDFsharp 6.2.4 không có API annotation đủ loại → chú thích được ghi bằng PDFsharp **ở tầng dictionary**: đủ khóa chuẩn (12.5), **luôn có `/AP`** sinh bằng XGraphics (để mọi trình đọc hiển thị như nhau), tô sáng nhân màu (`/BM /Multiply`), chữ tiếng Việt bằng phông đóng gói nhúng subset, thẳng theo góc xoay trang. `/AP`: `/BBox` = `/Rect` trong không gian trang, ma trận đơn vị; nét viết thẳng content stream (số invariant); chữ vẽ bằng `StampForms` rồi đặt bằng `FormToLocal.Then(mapper.ScreenToPdfMatrix)`. FreeText theo Acrobat: `/C` = màu nền, màu viền trong `/DA`. Trả lời/trạng thái = `/Text` ẩn có `/IRT` (`/State`, `/StateModel`). Mảng `/Annots` luôn được chép thành mảng riêng của trang.

**Chú thích của phiên — thao tác trang, tức thì**
- Chú thích mới nằm trên `PageRef.Annotations` (đi theo trang khi di chuyển/xoay/cắt/nhân bản; là nội dung: `HasSameContent`, `IsIdentity`), trong **không gian PDF của trang nguồn**. Thêm/sửa/xóa = `annotate.add/update/delete` (thao tác plan, một bước hoàn tác, tài liệu "chưa lưu"); manifest phiên lưu chúng (`PageDto.Annotations`); ảnh của con dấu ảnh nằm trong thư mục phiên.
- Mọi lần vật chất hóa (lưu, xuất, xem trước đóng dấu, đầu vào của thao tác nội dung) ghi chúng vào tệp bằng `AnnotationWriter` → sau một thao tác nội dung chúng trở thành chú thích có sẵn trong tệp.
- Hiển thị: ảnh trang do PDFium vẽ từ tệp nguồn (chưa có chúng) → `AnnotationLayer` vẽ đè bằng WPF (`AnnotationDrawing`, cùng hình như bản ghi; tô sáng vẽ trong suốt 40 % vì WPF không nhân màu); in ấn vẽ đè như vậy. **Thumbnail chưa hiện chú thích của phiên.**
- Công cụ (ribbon "Chú thích" + thanh công cụ trên trang): đánh dấu vùng chọn ngay hoặc bật công cụ rồi kéo qua chữ; ghi chú (bấm đặt, nhả chuột mở ô bình luận — Popup `StaysOpen=False` chỉ mở sau `Release`, bấm ra ngoài giữ chữ, Esc bỏ, Ctrl+Enter xong); hộp chữ (kéo khung → gõ → tự cao theo chữ); vẽ tay (mỗi lần kéo một nét); chữ nhật/elip, đường/mũi tên (kéo; bấm = cỡ mặc định); đa giác/đường gấp khúc (bấm thêm điểm, đúp xong); con dấu (ĐÃ DUYỆT, ĐÃ XEM, KHÔNG DUYỆT, KHẨN, MẬT, BẢN NHÁP, SAO Y BẢN CHÍNH hoặc chữ gõ, kèm ngày). Bấm chọn, kéo di chuyển (markup đứng yên trên chữ), Delete, màu, nét, "Bình luận…". Chuột: `DocumentView` gọi `Annotations.Press` trước, không trúng chú thích mới tới chọn chữ.

**Chú thích có sẵn trong tệp — thao tác nội dung (revision)**
- Đọc bằng PDFium (`PdfiumPage.GetAnnotations` → `FileAnnotation`: loại, `/Rect`, `/NM`, `/T`, `/Contents`, `/M`, `/IRT` → chỉ số, `/State`, `/F`, `QuadPoints`; mọi handle bọc `AnnotationSafeHandle`). Màu không đọc (PDFium trả lỗi khi có `/AP`).
- `annotate.editFile` (`EditFileAnnotationsOperation`): trả lời, đặt trạng thái, sửa nội dung, **di chuyển** (dời `/Rect`, `/QuadPoints`, `/Vertices`, `/L`, `/CL`, `/InkList`, `/Rect` của `/Popup`; `/AP` đi theo vì được khớp vào `/Rect`), **xóa** (cả luồng `/IRT` và `/Popup`). Chú thích được tìm theo chỉ số + loại + `/NM` (không có thì `/Rect` ±0,5 pt); tệp đổi mà không thấy thì **từ chối** chứ không sửa nhầm chú thích khác.
- Viewer: chú thích của tệp được đọc nền cho các trang đang hiện; bấm chọn (khung nét đứt), kéo khung theo chuột → nhả = revision mới; Delete xóa; Esc bỏ chọn. Panel Bình luận: trả lời, trạng thái, sửa nội dung, xóa. Mỗi thay đổi là một bước hoàn tác, chạy nền ở thanh trạng thái; bị chặn khi trình chỉnh sửa trang còn đối tượng chờ áp dụng. **Hộp chữ của tệp không sửa được chữ** (chữ nằm trong `/AP`).

**Panel "Bình luận" (sidebar, mục thứ 4; ribbon "Duyệt → Bình luận")**
- Chú thích của phiên + của tệp, theo trang rồi trên → dưới như hiển thị; trả lời/trạng thái của tệp gom theo `/IRT`. Lọc theo loại, tác giả, trạng thái, chữ (bỏ dấu như tìm kiếm). Chọn → cuộn tới (và chọn chú thích của phiên). Chỉ đọc khi panel hiện, đọc lại khi các trang đổi.
- Xuất (`CommentExport`): Markdown theo trang; CSV 1 dòng/bình luận + 1 dòng/trả lời, ngày ISO, chống công thức bảng tính (`'` đầu ô), UTF-8 có BOM.

**Làm phẳng (`annotate.flatten`, ribbon "Duyệt → Làm phẳng")**
- Vẽ `/AP /N` (hoặc trạng thái theo `/AS`) của chú thích bình luận vào nội dung trang qua `PageStamper` (BBox × Matrix khớp `/Rect`, 12.5.5; theo thứ tự `/Annots`; không nhân thêm `/CA` vì `/AP` đã mang độ mờ, đúng như PDFium hiển thị), rồi gỡ chúng cùng luồng trả lời và `/Popup`. Giữ liên kết, trường biểu mẫu, Redact và chú thích không có `/AP` (không có gì để vẽ); chú thích ẩn bị gỡ không vẽ.
- Hộp thoại: 4 loại (`AnnotationGroup`, mặc định tất cả) + trang (mặc định cả tài liệu; trang hiện tại / vùng chọn của Sắp xếp, lẻ, chẵn, khoảng). Một bước hoàn tác.

**Quyết định / giới hạn**
- **Lưu incremental để không phá chữ ký số** — làm ở P8 lát 8a (xem mục Phase 8 "Lưu tệp đã ký"): khi thay đổi chỉ là chú thích của phiên, chúng được nối vào tệp bằng cập nhật gia tăng; mọi thay đổi khác vẫn ghi lại toàn bộ và cảnh báo như P3.
- Panel không lọc theo màu (màu chú thích của tệp không đọc được qua PDFium khi có `/AP`).
- Thumbnail chưa vẽ chú thích của phiên; tô sáng trên màn hình vẽ bằng độ trong suốt thay vì nhân màu.
- Namespace `PdfPro.Operations.Annotating` (không phải `Annotations`: đụng `PdfGraph.Annotations(page)`). `PdfRect` có `[JsonConstructor]` và `[JsonIgnore]` cho thuộc tính tính toán (struct deserialize ra toàn 0 nếu thiếu).

**Nghiệm thu**
- Unit/golden: chú thích mọi loại ghi ra được PDFium đọc lại đúng loại/vị trí/trả lời/trạng thái và render đúng màu/chỗ; hộp chữ và con dấu trên trang /Rotate 0/90/180/270 có CropBox lệch gốc đứng thẳng như trang hiển thị (quad của tô sáng lấy từ hộp ký tự trong không gian trang nguồn nên không phụ thuộc góc xoay); chữ tiếng Việt trong hộp chữ/con dấu đọc lại đúng; làm phẳng xong trang render như lúc có chú thích (lệch < 0,5 % điểm ảnh), liên kết còn nguyên; di chuyển chú thích của tệp → nét mực vẽ ở chỗ mới; tìm "hoa" khớp "Hòa", "hóa" (chế độ dấu Tự động).
- Kịch bản FlaUI `Phase6AcceptanceTests` (chạy tay, `PDFPRO_UI_TESTS=1`): tìm "doc lap" (không dấu) trên `basic-vi.pdf` ra "Độc lập"; "Tô sáng" đánh dấu ngay kết quả đang chọn → panel Bình luận liệt kê nó; Lưu → tệp có `/Highlight` (đọc bằng PDFsharp); Làm phẳng là một bước hoàn tác, lưu xong không còn `/Highlight` và chữ các trang (PdfPig) y như ban đầu.
- Kiểm tay: lưu rồi mở bằng Acrobat Reader — chú thích hiển thị đúng và sửa được; tô sáng trên trang xoay 90° nằm đúng dòng chữ; mở `annotated.pdf` do Acrobat tạo — trả lời/xóa/di chuyển được; copy đoạn tiếng Việt dán vào Word đúng dấu, không vỡ dòng; tìm trên tệp 1.000 trang không treo UI và Dừng được.
- Số đo trên máy phát triển: chưa đo (ghi khi chạy `Phase6AcceptanceTests` trên Windows).

### Sửa nội dung gốc & biểu mẫu (Phase 7)

**Đối tượng của trang (PDFium → Core `Editing/PageObjects.cs`)**
- `PdfiumPage.GetObjects` liệt kê page object như PDFium thấy: chỉ số, loại (chữ, ảnh, đường, shading, form), hộp bao + 4 góc, ma trận, màu tô; chữ có tên phông, cỡ, nhúng hay không, đậm/nghiêng/serif theo cờ phông, chữ ẩn (lớp OCR); ảnh có cỡ điểm ảnh. PDF không có "đoạn văn": đơn vị sửa là **một text object** (một lần vẽ chữ — thường một dòng hoặc một đoạn dòng); UI ghi rõ "chỉ chính xác trong phạm vi một khối chữ".

**`edit.content` — thao tác nội dung chạy bằng PDFium**
- `EditContentParams` = danh sách `ContentEdit` (JSON đa hình): `ReplaceTextEdit` (tràn hoặc xuống dòng trong bề rộng cũ, đo bằng `FPDFFontGetGlyphWidth`), `DeleteObjectEdit`, `MoveObjectEdit`, `RotateObjectEdit` (quanh tâm hộp), `ReplaceImageEdit`. Đối tượng nhận theo `PageObjectRef` = trang + chỉ số + loại + hộp bao ±1 pt; tệp đã đổi mà không khớp thì **từ chối**, không sửa nhầm. Mọi đối tượng được tìm trước khi sửa cái nào.
- Thay chữ: `FontCoverage` (PDFsharp đọc ToUnicode bfchar/bfrange; WinAnsi khi phông không nhúng; còn lại coi như không biết) cho biết phông của object có đủ chữ không. Đủ → `FPDFTextSetText`, giữ phông/cỡ/màu/ma trận/chế độ vẽ. Thiếu (phông subset của Word thiếu chữ có dấu) → xóa object, tạo object mới cùng ma trận/cỡ/màu bằng phông đóng gói gần nhất (serif/sans, đậm/nghiêng) qua `FPDFTextLoadFont` (CID, nhúng đủ) và **báo đã thay phông** sau khi áp dụng.
- Ảnh: thay bằng ảnh khác giữ khung (mặc định giữ tỉ lệ, căn giữa); JPEG nhúng nguyên byte (`FPDFImageObjLoadJpegFileInline`), PNG giải mã bằng `PngCodec` tự viết (không thêm package); **luôn tạo object ảnh mới** ở cùng vị trí thứ tự vẽ vì XObject ảnh có thể dùng chung giữa các trang. Trích ảnh: DCT/JPX nguyên byte, còn lại PNG ở độ phân giải gốc.
- `FPDFPageGenerateContent` viết lại toàn bộ content stream của trang → chỉ gọi cho trang có sửa; `FPDF_SaveAsCopy` ghi revision mới (một bước hoàn tác).
- Viewer "Sửa nội dung": rê = khung, bấm = chọn, kéo = di chuyển, đúp/Enter = gõ chữ tại chỗ (Popup trên đúng hộp chữ; Enter xong, Shift+Enter xuống dòng, Esc bỏ, bấm ra ngoài giữ), Delete, xoay, Thay ảnh…, Lưu ảnh…; nền phủ > 80 % trang chỉ chọn được khi không còn gì khác; chữ OCR ẩn bỏ qua.

**Biểu mẫu — mô hình và ghi (PDFsharp ở tầng dictionary)**
- `FormReader`: cây `/Fields` (thuộc tính kế thừa `/FT /Ff /V /DV /DA /Q /Opt /MaxLen`), widget gộp hoặc `/Kids`, trang của widget, `/Opt` cặp, on-state từ `/AP /N`, cỡ chữ của `/DA`, định dạng số/ngày từ `/AA /F` (AFNumber_Format, AFDate_FormatEx), họ phông đóng gói từ phông trong `/AP`, chữ trên nút từ `/MK /CA`, XFA (chỉ XFA / XFA kèm AcroForm).
- `FormWriter` + `FormAppearances`: `/AcroForm` với `/Helv` và `/ZaDb` trong `/DR`; `/DA` ghi `/Helv` để trình khác vẫn gõ được, còn `/AP` **luôn vẽ bằng phông đóng gói** (Noto Sans mặc định; Noto Serif, Be Vietnam Pro chọn được) nên tiếng Việt hiển thị/in đúng ở mọi trình đọc; `/NeedAppearances` bị xóa. Chữ 1 dòng/nhiều dòng/mật khẩu/comb, dấu tích, chấm radio, danh sách có dòng chọn, nút; trang `/Rotate` thì `/AP` xoay theo (`/MK /R`, `/Matrix`). Tên phân cấp `a.b` = cha "a" + `/T` "b" (tên bộ phận không có dấu chấm); `/Ff` luôn ghi (vì kế thừa); giá trị ban đầu cũng là `/DV`.
- Định dạng (`Core/Forms/FormTextFormat`): số (số lẻ, dấu phân cách 1.234,56 hoặc 1,234.56) lưu dạng thường "1234.5", hiển thị có định dạng; ngày theo 5 mẫu (`dd/mm/yyyy`…) lưu theo mẫu. Ghi thành `/AA /K` + `/F` (`AFNumber_Keystroke/Format`, `AFDate_KeystrokeEx/FormatEx`) như Acrobat.
- **PDFium không vẽ widget trong `FPDF_RenderPageBitmap`** — chỉ qua `FPDF_FFLDraw` với môi trường form-fill: tài liệu có biểu mẫu được tạo môi trường (không callback, version 1) ở lần render đầu và vẽ thêm bằng `FPDF_FFLDraw`; struct `FPDF_FORMFILLINFO` của PDFiumCore không xóa bộ nhớ native nên mọi callback được đặt null tường minh.

**Điền biểu mẫu — thao tác trang, tức thì**
- Giá trị điền nằm trên `PagePlan.FormValues` (cấp tài liệu, theo tên đầy đủ; đi theo mọi chỉnh sửa trang; là nội dung nhưng không đổi `HasSamePages`), `forms.fill` = một bước hoàn tác; manifest phiên lưu chúng. Khi vật chất hóa, `FormWriter.SetValues` ghi `/V`, `/AS`, `/I` và vẽ lại `/AP` trong tài liệu gốc (field của nguồn chèn thêm không được gộp). Không dùng trạng thái form-fill của PDFium (không hoàn tác được, phông `/DA` thường thiếu chữ có dấu).
- Viewer (`FormFieldLayer`): ô nhập WPF thật đặt lên widget qua `PageCoordinateMapper` — ô chữ (giá trị ghi khi rời ô; Enter sang ô sau), mật khẩu, ô tích/nút chọn, danh sách thả (gõ được khi cờ Edit), danh sách (chọn nhiều); Tab theo thứ tự field; chữ NFC, cắt theo `/MaxLen`; nhận chuột chỉ khi không có chế độ khác dùng viewer. Lưu khi còn trường bắt buộc trống: liệt kê tối đa 10 → "Vẫn lưu" / "Quay lại điền". XFA: InfoBar giải thích; biểu mẫu chỉ XFA không thiết kế được.
- `forms.reset` ("Xóa dữ liệu"): mọi trường về `/DV` (không có thì rỗng/Off), một bước hoàn tác.

**Thiết kế biểu mẫu (`forms.design` — thao tác nội dung, mỗi thay đổi một revision)**
- Thứ tự: `Remove` (gỡ field + widget khỏi `/Annots`, cha rỗng và `/CO` theo) → `Widgets` (dời/đổi cỡ/gỡ từng widget; chỉ dời thì giữ nguyên `/AP`, đổi cỡ thì vẽ lại; gỡ widget cuối = gỡ field) → `Update` (sửa thuộc tính **tại chỗ**: tên (cả đổi cha), cờ, `/TU`, cỡ trong `/DA` (giữ tên phông), `/Q`, `/MaxLen`, `/Opt`, chỉ thay `/K` `/F` của `/AA` (action khác như tính toán giữ nguyên), giá trị `/V` + `/DV`, chữ nút, họ phông; đổi loại bị từ chối) → `Add`. Tên trùng (cả trùng với cha/con) bị từ chối; field không còn trong tệp thì báo lỗi.
- Viewer "Thiết kế biểu mẫu": khung + tên cho mọi widget; công cụ Ô văn bản / Ô đánh dấu / Nút chọn / Danh sách thả / Danh sách / Nút / Chữ ký — kéo khung để đặt, bấm = cỡ thường dùng (theo màn hình, đúng cả trang xoay), tên tự đặt; nút chọn khi đang chọn một nhóm thì vào nhóm đó. Chọn, kéo dời, kéo góc đổi cỡ, Delete (field, hoặc một nút của nhóm), Esc. Panel Thuộc tính (bên phải): tên, chú thích, bắt buộc, chỉ đọc, nhiều dòng, mật khẩu, số ký tự, định dạng, giá trị mặc định, lựa chọn, chữ nút, phông, cỡ, căn — lỗi giải thích bằng tiếng Việt trước khi ghi. Sau revision, field vừa đổi được chọn lại theo tên.
- **Nhận diện trường** (`forms.autodetect`, Core `FormAutodetect`): chuỗi ≥ 4 dấu chấm/gạch dưới/"…" → ô văn bản (nhãn = chữ trước nó); đường kẻ ngang không gạch dưới chữ → ô văn bản đứng trên đường kẻ; ô vuông vẽ 6–24 pt hoặc ký tự ☐ □ ❑ ❒ ▢ → ô đánh dấu (nhãn = chữ sau nó). Bỏ chỗ đã có field. Đề xuất hiện màu cam, bấm để chọn/bỏ, "Thêm các trường đã chọn" = một revision (tên = nhãn, chú thích = nhãn).

**Dữ liệu biểu mẫu**
- Xuất/Nhập: JSON (một object; object lồng nhau = tên phân cấp; mảng = lựa chọn của danh sách; số/true/false đọc thành chữ; null xóa) và XFDF (ISO 19444-1; đọc không DTD/thực thể ngoài). Nhập = một bước hoàn tác; tên không có và giá trị không hợp được liệt kê. Khớp dữ liệu (`FormData`): tên đúng, không thì không phân biệt hoa/thường (NFC); ô đánh dấu nhận on-state hoặc chữ có/không ("x", "1", "true", "có", "không"…); nhóm chọn một on-state; danh sách theo giá trị xuất hoặc chữ hiển thị; số/ngày theo định dạng.
- **Trộn dữ liệu** (`forms.merge`): bảng CSV (dấu phân cách theo dòng tiêu đề; UTF-8 có/không BOM, UTF-16, còn lại Windows-1258 của Excel cũ) hoặc **.xlsx tự đọc** bằng `System.IO.Compression` + XML (không thêm DocumentFormat.OpenXml: sheet đầu, chuỗi dùng chung/inline/rich, số, ngày theo định dạng số, true/false); tối đa 10.000 dòng, 500 cột. Mỗi dòng một tệp, tên theo cột chỉ định (làm sạch, trùng thì "(2)"; trống thì "tên mẫu (dòng)"), danh sách tách bằng ";", tùy chọn làm phẳng; ghi tệp tạm rồi mới đổi tên (lỗi giữa chừng không để lại gì). Mẫu = tệp của tài liệu nếu không có gì chưa lưu, ngược lại bản sao của tài liệu như đang hiển thị. Chạy nền, hủy được; báo cột lạ và giá trị không hợp.

**Làm phẳng biểu mẫu (`forms.flatten`)**
- Vẽ `/AP /N` (trạng thái `/AS` với nút) của mọi widget vào nội dung trang theo thứ tự `/Annots` (`AppearancePlacement`, dùng chung với làm phẳng chú thích), widget thiếu `/AP` — hoặc tất cả khi `/NeedAppearances` — được vẽ trước như trình đọc hiển thị; widget ẩn chỉ gỡ; **chữ ký số đã ký giữ nguyên** (vẽ vào sẽ phá chữ ký). Hết field thì gỡ `/AcroForm`. Hỏi xác nhận, một bước hoàn tác.

**Quyết định / giới hạn**
- Sửa chữ chỉ chính xác trong một text object; chữ thay phông không tự canh lại các object khác trên dòng.
- Field của tài liệu chèn từ nguồn khác không được gộp vào biểu mẫu của tài liệu.
- Sửa thuộc tính không đổi được giá trị xuất (on-state) của nút chọn có sẵn — xóa nút rồi thêm lại.
- Tên field theo nhãn có thể có dấu cách và chữ có dấu (hợp lệ trong PDF); dấu chấm trong nhãn bị thay bằng dấu cách.
- Thumbnail và in ấn chưa vẽ giá trị điền trong phiên cho tới khi lưu (ảnh trang do PDFium vẽ từ tệp nguồn).
- Định dạng số/ngày chỉ kiểm khi điền trong PDF_Pro và khi nhập/trộn dữ liệu; trình đọc khác dùng các action AF* đã ghi.

**Nghiệm thu**
- Unit/golden: sửa chữ trên tệp tiếng Việt → PdfPig đọc ra chữ mới, chữ cũ không còn; phông thiếu chữ → thay phông, vẫn đọc đúng; vùng không sửa render y nguyên; ảnh thay/xoay/trích đúng cỡ; mọi loại field ghi ra được PDF_Pro, PdfPig và PDFium đọc lại (giá trị, cờ, lựa chọn, on-state), render đúng chỗ; định dạng, phông, `/DV`, action tính toán được giữ khi sửa tại chỗ; JSON/XFDF khứ hồi; CSV/XLSX đọc đúng (cả Windows-1258); trộn 100 dòng → 100 tệp đúng dữ liệu; làm phẳng → giá trị thành chữ của trang, không còn `/AcroForm`; tham số `forms.*` khứ hồi qua manifest phiên.
- Kịch bản FlaUI `Phase7AcceptanceTests` (chạy tay, `PDFPRO_UI_TESTS=1`): tờ khai in sẵn (chấm, gạch dưới, ô vuông) → "Nhận diện trường" ra 3 đề xuất → thêm → điền "Nguyễn Văn An" + tích ô → Lưu (giá trị trong tệp) → Làm phẳng (hỏi, một bước hoàn tác) → Lưu: không còn field, tên là chữ của trang; trộn bảng CSV 100 dòng → 100 tệp, mỗi tệp đúng dòng của nó.
- Kiểm tay: sửa một dòng trong tệp Word xuất PDF tiếng Việt — hiển thị đúng, tìm được chuỗi mới; biểu mẫu tạo bằng PDF_Pro mở và điền được trong Acrobat Reader và Chrome, nhóm nút chọn chỉ chọn được một; làm phẳng rồi mở bằng Acrobat Reader — không sửa được nữa.
- Số đo trên máy phát triển: chưa đo (ghi khi chạy `Phase7AcceptanceTests` trên Windows).

### Bảo mật, xóa vĩnh viễn & chữ ký số (Phase 8)

> Đang làm theo lát (xem `docs/PROGRESS.md`); mục này ghi các quyết định đã chốt và được bổ sung ở mỗi lát.

**Mật khẩu & quyền (`security.encrypt`, `security.decrypt`, `security.protection`)**
- Mô hình (Core `Security/`): `DocumentSecurity` = mật khẩu mở (tùy chọn), mật khẩu chủ (bắt buộc, khác mật khẩu mở), AES-256 (mặc định, R6) hoặc AES-128 (R4, trình đọc cũ), quyền `DocumentPermissions` (in thấp/cao, sửa, sao chép, chú thích, điền biểu mẫu, ghép trang). Quyền "trợ năng" **luôn bật** (PDF 2.0 bỏ quyền này; trình đọc màn hình luôn đọc được). Mật khẩu ≤ 127 byte UTF-8; AES-128 chỉ nhận ASCII in được, ≤ 32 ký tự (R4 cắt/đệm 32 byte — dài hơn sẽ mất đuôi âm thầm). `ToString` không bao giờ hiện mật khẩu.
- `PasswordStrength`: bit ≈ log2(bảng chữ) × độ dài, trừ cho lặp, dãy phím/chữ cái (1234, abcd, qwerty), ngày tháng, mật khẩu phổ biến (cả "matkhau", "anhyeuem"…); dưới 8 ký tự không quá "Yếu". Hộp thoại hiện thước + lời khuyên tiếng Việt; mật khẩu yếu được cảnh báo một lần, bấm "Áp dụng" lần nữa thì vẫn dùng.
- Ghi bằng **PDFsharp 6.2.4** (`SecuritySettings`, `SetEncryptionToV5` / `SetEncryptionToV4UsingAES`, `SetEncryptionToNoneAndResetPasswords`). PDFsharp tự chặn sửa tệp bảo vệ khi chỉ có mật khẩu mở ("owner password is required") → `EncryptionWriter.OpenForChange` dịch thành `DocumentPasswordException` (`WrongPassword` / `OwnerPasswordRequired`). Mở bằng mật khẩu chủ, PDFsharp **bỏ hẳn mã hóa** khi lưu → bảo vệ luôn phải áp lại tường minh.
- `security.encrypt` / `security.decrypt` (Export, cho tệp/lô/CLI): ghi bản sao vào thư mục ra, qua tệp tạm rồi đổi tên — lỗi một tệp thì không tệp nào xuất hiện. Gỡ bảo vệ chỉ với mật khẩu chủ.
- **Tài liệu đang mở**:
  - Mở bằng mật khẩu mở → **chỉ xem** như P3 (in/sao chép theo quyền của tác giả: Ctrl+C bị từ chối khi tệp cấm sao chép). Hộp thoại "Mật khẩu & quyền" cho nhập mật khẩu chủ → tài liệu được mở lại **có thể sửa** ngay trong tab đó, cùng trang.
  - Mở bằng mật khẩu chủ (gõ ở hộp hỏi mật khẩu, hoặc mở khóa như trên): bản sao `s0` trong thư mục phiên được **ghi lại không mã hóa** (thư mục phiên thuộc người dùng, bị xóa khi đóng tài liệu) để mọi phần (vật chất hóa, biểu mẫu, sửa nội dung, thumbnail) đọc như tệp thường; `PagePlan.Protection` (`ProtectionSetting`: thuật toán, quyền, có mật khẩu mở không, `PasswordsId`) ghi bảo vệ của tệp để **lưu lại vẫn được bảo vệ** (RC4 R2/R3 lên AES-256, R4 giữ AES-128).
  - **Mật khẩu chỉ nằm trong bộ nhớ** (`ProtectionPasswordVault` của workspace, theo `PasswordsId`); `ProtectionSetting` không chứa mật khẩu nên vào được lịch sử hoàn tác và manifest. Đổi/gỡ bảo vệ = thao tác trang `security.protection` (một bước hoàn tác, tên "Đặt mật khẩu & quyền" / "Gỡ bảo vệ"), áp dụng khi lưu. Cung cấp mật khẩu còn thiếu cho cùng bảo vệ không phải là một bước.
  - Khi lưu: `DocumentSaver` lấy `DocumentSecurity` từ vault → `MaterializeRequest.Security` → bộ vật chất hóa mã hóa trước khi ghi; tệp tạm được mở lại bằng mật khẩu mở và **phải là tệp mã hóa**, không thì không thay tệp gốc. Thiếu mật khẩu (phiên khôi phục sau sự cố; mở bằng mật khẩu chủ nên chưa biết mật khẩu mở) → hộp thoại hỏi trước khi lưu; bỏ qua thì **không lưu** — không bao giờ lưu mất bảo vệ do nhầm. Vật chất hóa cho sửa nội dung/xem trước/xuất trang thì không mã hóa (đầu vào của thao tác khác); revision mới giữ `Protection`.
- PDFium: `FPDF_GetDocPermissions` trả mọi bit quyền (bit 1–2 bằng 0, `0xFFFFFFFC`) khi mở bằng mật khẩu chủ — **giống hệt** tệp cho phép mọi quyền mở bằng mật khẩu mở → `HasEveryPermission` chỉ là ứng viên, PDFsharp quyết định (mật khẩu không phải của chủ → tài liệu vẫn chỉ xem). `FPDF_GetDocUserPermissions` cho quyền gốc của tệp (`UserPermissions`) dù mở bằng mật khẩu nào.

**Xóa vĩnh viễn (`security.redact`)**
- Đánh dấu trước, áp dụng sau: chế độ "Xóa thông tin" trên viewer (ribbon `security.redact`, loại trừ với các chế độ khác dùng chuột) — công cụ **Vùng** (kéo khung, cắt theo khung trang; bấm không kéo = chọn vùng đã đánh dấu, Delete xóa, Esc bỏ chọn) và **Chữ** (kéo chọn chữ → mỗi trang một vùng gồm hình chữ nhật theo dòng), "Đánh dấu vùng chọn", "Tìm & đánh dấu" theo mẫu. Vùng đánh dấu (`RedactionMark`) nằm trong không gian PDF của trang nguồn (= của trang sau khi vật chất hóa), đi theo trang khi sắp xếp, mất khi trang bị xóa; chưa đổi gì trong tài liệu, không vào lịch sử (khung đỏ trên nền mờ).
- Mẫu tìm (Core `RedactionFinder`, chạy trên lớp ký tự đã chuẩn hóa NFC): CCCD 12 số, SĐT di động/cố định (0 / +84), email, số tài khoản 9–19 số (không phải CCCD/SĐT), ngày d/m/yyyy (tắt mặc định), biểu thức riêng (timeout 2 s, quá thời gian thì bỏ biểu thức đó và báo). Hai mẫu trùng chỗ thì mẫu đứng trước giữ. Kết quả là dữ liệu cá nhân: chỉ hiện trong danh sách xem lại (tích/bỏ từng mục), `ToString` không lộ chữ, không bao giờ ghi log.
- "Áp dụng": hộp thoại xác nhận (nói rõ những gì mất vĩnh viễn, trang thành ảnh, chỉ hoàn tác được **trước khi lưu**; màu ô đen/trắng/xám, nhãn tùy chọn — mặc định "Đã xóa theo quy định") → thao tác nội dung `security.redact` = revision mới = **một** bước hoàn tác trong phiên; xong thì vùng đánh dấu được xóa và báo cáo (số vùng, trang, chú thích/trường/bookmark/metadata đã gỡ). Lưu khi còn vùng chưa áp dụng → hỏi "Áp dụng rồi lưu" / "Lưu, giữ đánh dấu" / Hủy; đóng tab/thoát → hỏi bỏ vùng đánh dấu.
- Hai chế độ (`RedactParams.Mode`), chọn trong hộp thoại áp dụng (theo PROMPTS P8 B2: "An toàn tuyệt đối" là mặc định, "Giữ vector" là nâng cao):
  - **Giữ vector** (nâng cao, lát 4; `PdfiumDocument.RedactPageKeepingVectors`): chỉ phần dưới vùng bị bỏ, phần còn lại của trang giữ nguyên chữ (chọn/tìm/sao chép được) và hình vẽ. Ký tự có glyph chồng lên vùng (> 0,1 pt mỗi chiều; ký tự không mực như dấu cách: theo tâm hộp dòng) bị bỏ — chữ còn lại của text object được viết lại thành các đoạn mới cùng phông/cỡ/màu/ma trận, từng ký tự đặt đúng chỗ cũ bằng `FPDFText_SetPositions` (dấu cách đầu/cuối đoạn bỏ đi: không có mực, PDFium tự sinh lại khi trích). Đường nét chỉ-stroke cắt đúng tại mép vùng (`RectClipping.Outside`), hình chữ nhật tô bị khoét (`RectClipping.Subtract`; đường là một chữ nhật thì các mảnh là đối tượng chữ nhật để PDFium vẽ mép trùng điểm ảnh như `re`), subpath khác chồng vùng bị bỏ cả. Ảnh: mọi điểm ảnh chạm vùng bị tô màu ô trong dữ liệu ảnh, ghi thành ảnh mới cùng chỗ (stream cũ có thể dùng chung). Ô màu vẽ bằng PDFium. Thuộc tính marked content (`/ActualText`, `/Alt`…) nhắc tới nội dung bị xóa bị bỏ **trước** khi đọc chữ (PDFium đọc `/ActualText` thay cho ký tự).
  - **Hậu kiểm theo tọa độ** của trang vừa ghi (đọc lại từ content stream mới): không ký tự nào dưới vùng; mọi ký tự khác (trừ khoảng trắng) còn đúng giá trị và gốc (±0,3 pt); ảnh render 100 DPI không chú thích giống trước ngoài vùng (+2 px) và ngoài phần đã bỏ (ngưỡng 48/255, cho phép 0,02 % điểm ảnh); ảnh viết lại so theo trung bình khối 8×8 (PDFium làm mượt ảnh gốc có `/Interpolate`, ảnh mới thì không). Trang không đạt — hoặc có chữ/hình của form XObject (dùng chung giữa các trang) dưới vùng, chữ không đọc được từng ký tự hay không chạy ngang, đối tượng lạ, ảnh không giải mã được — **tự chuyển sang chế độ ảnh** (vẽ từ tài liệu gốc), báo cáo liệt kê các trang đó.
  - **Ảnh — an toàn tuyệt đối** (mặc định, lát 3a; tùy chọn OCR lại phần còn lại để tìm kiếm được: để P10 khi có Tesseract): trang có vùng được PDFium vẽ 300 DPI **không chú thích** (≤ 40 MP; trang xám ghi DeviceGray), tô vùng trong ảnh, nội dung trang **thay hẳn** bằng ảnh đó + ô màu vector; `/Resources` mới, gỡ `/StructParents`, `/B`. Trang không có vùng giữ nguyên vector.
  - Mọi trang có vùng mất `/Thumb`, `/PieceInfo`, `/Metadata`; nhãn (StampForms, thẳng theo góc xoay) vẽ sau cùng. PDFsharp bỏ mọi đối tượng không còn được tham chiếu khi lưu, nên dữ liệu gốc (content stream, ảnh cũ) không nằm lại trong tệp.
- Dọn chỗ khác chứa nội dung bị xóa (`RedactionCleanup`): chú thích/liên kết đè vùng hoặc có chữ/URI chứa nó (cả luồng `/IRT` + `/Popup`), trường biểu mẫu có widget đè vùng hoặc giá trị chứa nó, bookmark có tiêu đề chứa nó (mục con được nâng lên), mục Info + XMP chứa nó, chữ của cây thẻ (`/Alt`, `/ActualText`, `/E`, `/T` của phần tử cấu trúc). So khớp NFC, không phân biệt hoa/thường, gộp khoảng trắng, số so theo chữ số; chuỗi < 4 ký tự bỏ qua (tránh xóa nhầm diện rộng).
- **Hậu kiểm bắt buộc** bằng bộ trích độc lập (PdfPig), trước khi vẽ nhãn: trang giữ vector còn chữ có tâm glyph trong vùng → **tự chuyển trang đó sang ảnh** (vẽ từ tài liệu gốc) rồi kiểm lại, ghi vào báo cáo; trang ảnh còn bất kỳ chữ nào → **không ghi gì** và báo lỗi.

**Làm sạch tài liệu (`security.sanitize`)**
- Hộp thoại chọn mục (mặc định tất cả trừ liên kết ngoài; cảnh báo khi tài liệu có chữ ký số vì chữ ký sẽ mất hiệu lực) → thao tác nội dung = revision mới = một bước hoàn tác; xong hiện báo cáo từng loại đã gỡ, nút "Lưu báo cáo…" ghi tệp .txt (UTF-8).
- Gỡ: Info + mọi luồng XMP (`/Metadata` ở mọi đối tượng); JavaScript (cây tên `/JavaScript`, `/OpenAction`, `/A`, `/AA` của catalog/trang/chú thích/trường, chuỗi `/Next`, `/JS` của Rendition, `/XFA` + `/CO` của AcroForm); tệp đính kèm (`/EmbeddedFiles`, chú thích `/FileAttachment`, `/AF`, `/Collection`, hành động `GoToE`); hành động `SubmitForm`/`ImportData`/`Launch`; tùy chọn liên kết ngoài (`URI`, `GoToR`); liên kết mất hành động và không có đích thì gỡ luôn chú thích; lớp ẩn mặc định (`/OCProperties /D`: `/OFF`, hoặc `/BaseState /OFF` trừ `/ON`; OCMD theo `/P`, `/VE` không đánh giá = coi là hiện): đoạn `/OC … BDC … EMC`, lệnh `Do` của XObject có `/OC` ẩn (trong trang và form XObject lồng nhau), chú thích có `/OC` ẩn, rồi gỡ nhóm khỏi mọi danh sách cấu hình; đối tượng nằm **hoàn toàn** ngoài vùng hiển thị (PDFium, trừ chữ dùng làm vùng cắt); `/Thumb`; `/PieceInfo` (+ `/LastModified`), `/SpiderInfo`. Tệp luôn ghi lại toàn bộ → bỏ các revision cũ (đếm %%EOF, trừ tệp linearized) và dữ liệu sau %%EOF.
- Gỡ đoạn nội dung bằng bộ tách token riêng (`ContentSections`: chuỗi, hex, mảng/từ điển, chú thích, tên có `#xx`, ảnh nội tuyến BI…ID…EI) chỉ xóa đúng khoảng byte, phần còn lại giữ nguyên; đoạn làm lệch q/Q hoặc BT/ET thì giữ lại. **Hậu kiểm**: trang có đổi nội dung được PDFium render (100 DPI, không chú thích) so với bản gốc (PDFium đã ẩn lớp tắt); trang khác đi (ví dụ lớp ẩn đổi màu cho phần sau) → làm lại, giữ nguyên trang đó và ghi tên trang vào báo cáo; còn trang bị giữ thì không gỡ lớp khỏi danh sách.
- **Lưu bằng PDFsharp không còn "tự xưng"** (`PdfWriting.Save`, dùng cho mọi lần lưu: vật chất hóa, thao tác nội dung, mã hóa, Bates, trộn biểu mẫu…): PDFsharp 6.2.4 luôn thêm `/Creator`, `/Producer` (tài liệu mới: cả `/CreationDate`), một dòng `% Created with PDFsharp…` sau trailer và **thay `/Metadata` của catalog bằng gói XMP của nó** (tên PDFsharp, giờ lưu). Sau khi lưu, tệp được vá tại chỗ bằng byte cùng độ dài (bảng xref không đổi): mục Info do PDFsharp thêm và dòng chú thích thành dấu cách, `/Metadata` trỏ lại XMP gốc của tài liệu (giữ được nhờ một khóa tạm `/PdfProOwnMetadata` trong lúc lưu, xong thì xóa trắng) hoặc bị bỏ, gói XMP của PDFsharp bị xóa trắng. Nhờ vậy làm sạch xong rồi lưu, Thuộc tính tài liệu không còn tác giả, ngày tạo, phần mềm tạo.

**Ký số (`security.sign`, lát 6a: phần lõi)**
- Ký = **cập nhật gia tăng** tự viết (`Signing/IncrementalUpdate`): tệp gốc giữ nguyên từng byte, nối thêm đối tượng mới/đổi (số đối tượng gốc giữ nguyên — PDFsharp đọc không đánh số lại), rồi bảng xref (hoặc **xref stream** nếu tệp gốc dùng xref stream) + trailer `/Prev`. Đối tượng được ghi bằng `PdfObjectWriter` tự viết (tên `#xx`, chuỗi dạng hex, `PdfLiteral` để chèn tham chiếu "n 0 R").
- Bản cập nhật gồm: từ điển chữ ký (`/Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached`, `/M`, `/Name` = CN của chứng thư, `/Reason`, `/Location`, `/ContactInfo`), trường chữ ký gộp widget (`/F` in + khóa) hoặc ký vào **trường chữ ký trống có sẵn** (thêm `/V`, vẽ lại `/AP`), trang có `/Annots` thêm widget, `/AcroForm` (`/Fields`, `/SigFlags 3`; tạo mới khi chưa có). Chữ ký ẩn = widget `/Rect [0 0 0 0]`.
- Hình hiển thị: ảnh chữ ký tay (P5) bên trái, các dòng chữ (do App soạn sẵn theo ngôn ngữ người dùng: "Ký bởi…", ngày, lý do, nơi ký) bên phải bằng Noto Sans; vẽ bằng `StampForms` trong tài liệu nháp → lưu để PDFsharp nhúng phông → đọc lại → xuất cả đồ thị đối tượng với số mới; trang xoay thì `/Matrix` xoay như biểu mẫu P7.
- CMS tách rời bằng `SignedCms` (SHA-256, kèm chuỗi chứng thư, thuộc tính signing-time), `/ByteRange` vá sau khi ghi (độ rộng cố định), `/Contents` để chỗ 16 KB rồi tự nới và ký lại khi chuỗi/timestamp cần hơn. Timestamp RFC 3161 tùy chọn qua `ITimestampClient` (`HttpTimestampClient`: POST `application/timestamp-query`, 30 s) → thuộc tính không ký `id-aa-signatureTimeStampToken`.
- Từ chối có lý do (`SigningException.Failure`): tệp mã hóa (gỡ mật khẩu trước), chứng thư không có khóa riêng, trường đã ký, trường không phải chữ ký, trang không có, TSA lỗi. `SignOperation` (Export): ký bản sao nhiều tệp vào thư mục ra; `SignFileAsync` ghi tệp tạm cùng thư mục rồi thay — lỗi thì tệp đích nguyên vẹn.
- Kiểm (`SignatureVerifier`, phần lõi của lát 7): PDFium đọc chữ ký (`FPDFSignatureObj_*`), `/ByteRange` phải bao quanh đúng `/Contents`, `SignedCms.CheckSignature` trên đúng các byte đã ký, timestamp phải khớp chữ ký (`VerifySignatureForSignerInfo`), "phủ toàn tệp" hay có cập nhật sau.

**Xác minh chữ ký (`SignatureVerifier`, lát 7a: phần lõi)**
- Mỗi chữ ký PDFium đọc được: `/ByteRange` + CMS (như lát 6a) → **toàn vẹn**; thuật toán băm ("SHA-256"…) và khóa ("RSA 2048", "ECDSA 256"); trường + trang + khung của widget, `/Location`, `/ContactInfo` (PDFsharp đọc trường chữ ký, khớp với chữ ký của PDFium **theo `/ByteRange`** vì PDFium không cho biết trường).
- **Chuỗi chứng thư** (`CertificateTrustCheck`, `X509Chain`): thêm các chứng thư chữ ký mang theo; kiểm tại thời điểm dấu thời gian (có) hoặc bây giờ; thu hồi Online (CRL/OCSP, chờ tối đa 10 s, bỏ qua gốc) khi được phép; kết quả `Trusted` / `UntrustedRoot` (tự cấp, nhà cung cấp chưa cài) / `NotTimeValid` / `Revoked` / `Invalid`; không kiểm được thu hồi (không mạng, chứng thư không có địa chỉ) **không** làm hỏng kết quả mà ghi `RevocationChecked = false`. Test dùng `TrustOptions.TrustedRoots` (CustomRootTrust) với CA thử tạo bằng `CertificateRequest`.
- **Thay đổi sau khi ký** (`SignedRevisionChanges`): mở bản đã ký (tệp cắt tới hết `/ByteRange`) và tệp hiện tại bằng PDFsharp, so: số chữ ký đã ký tăng → `Signatures`; giá trị/số trường biểu mẫu (không kể chữ ký), trường chữ ký trống mới → `FormFields`; chú thích không phải widget/popup (loại, `/Rect`, `/Contents`, `/M`, `/AP`) → `Annotations`; số trang, `/MediaBox` `/CropBox` `/Rotate` `/Resources`, byte content stream → `Pages`; đọc không được → `Unknown`. Đếm số lần cập nhật sau chữ ký (số `%%EOF`).
- **Kết luận** (`SignatureVerdict`): không toàn vẹn, chứng thư bị thu hồi hoặc chuỗi hỏng → **Không hợp lệ (đỏ)**; chứng thư không được tin cậy/hết hạn, hoặc sau khi ký có thay đổi khác ngoài chữ ký mới → **Cần lưu ý (vàng)**; còn lại → **Hợp lệ (xanh)** — ký nhiều người lần lượt vẫn xanh.
- `PdfSigner` không còn dùng `WholeChain` của `CmsSigner` (nó **từ chối ký** khi máy thiếu chứng thư cấp trên, và lỗi bị báo nhầm thành "không có khóa riêng"): tự dựng chuỗi, kèm mọi chứng thư cấp trên tìm được.

**Lưu tệp đã ký (lát 8a, nợ P6 B9)**
- `DocumentSaver.KeepsSignatures`: tài liệu gốc có chữ ký số, phiên vẫn ở bản gốc (chưa có thao tác nội dung), không đặt mật khẩu, và plan **chỉ thêm chú thích** (`PagePlan.AddsOnlyAnnotations`: mọi trang của nguồn đúng thứ tự, không xoay/cắt/giá trị biểu mẫu, có ít nhất một chú thích) → Lưu/Lưu thành **nối chú thích vào tệp bằng cập nhật gia tăng** (`IncrementalAnnotations.Append`) thay cho vật chất hóa, và **không** hỏi cảnh báo "tệp đã ký". Còn lại vẫn ghi toàn bộ + cảnh báo như cũ.
- Cách nối: `AnnotationWriter` vẽ chú thích lên các trang trống của một tài liệu nháp (lưu để PDFsharp nhúng phông, đọc lại), xuất đồ thị đối tượng của chúng với số mới (không đi theo trang; `/P` trỏ về trang thật), rồi trang của tệp được ghi lại với `/Annots` = chú thích cũ + mới, số đối tượng gốc. Tệp mã hóa hoặc không đọc được → ghi toàn bộ như cũ (có log).
- Giới hạn: điền biểu mẫu sau khi ký vẫn ghi toàn bộ (giá trị + `/AP` vẽ lại cần phông nhúng — chưa làm gia tăng); chữ ký chứng nhận cấm mọi thay đổi (DocMDP P=1) không được kiểm trước.

**Panel "Chữ ký" (`security.verify`, lát 7b)**
- Mục thứ 5 của sidebar ("Chữ ký"); nút ribbon "Xác minh" mở nó. Mỗi tab có `SignaturesViewModel`: **chỉ kiểm khi panel đang hiện**, kiểm **tệp đã lưu** của tài liệu (đọc cả tệp, ≤ 1 GB) ở luồng nền qua `ISignatureVerification` (`SignatureVerifier` với cách máy này tin chứng thư, thu hồi Online); kiểm lại khi tệp trên đĩa đổi (đường dẫn, độ dài, giờ ghi) hoặc bấm "Kiểm lại"; tài liệu có thay đổi chưa lưu → ghi chú "thay đổi chưa lưu không nằm trong chữ ký nào".
- Mỗi chữ ký: biểu tượng + chữ **Hợp lệ (xanh) / Cần lưu ý (vàng) / Không hợp lệ (đỏ)**, người ký (CN) và tổ chức (O), trang (hoặc "Chữ ký ẩn"), rồi các dòng giải thích tiếng Việt: toàn vẹn; thay đổi sau khi ký (không đổi / chỉ thêm chữ ký / biểu mẫu / bình luận / nội dung trang / không xác định); tin cậy chứng thư (gốc nào, không tin cậy trên máy này, hết hạn tại thời điểm kiểm, bị thu hồi, chuỗi hỏng, chưa kiểm được thu hồi); dấu thời gian (máy chủ xác nhận) hoặc thời gian ký (đồng hồ máy người ký); lý do, nơi ký, thuật toán, tên trường. Dòng tóm tắt "n chữ ký: a hợp lệ, b cần lưu ý, c không hợp lệ". Chọn một chữ ký → viewer tới trang của nó (khi không có thay đổi chưa lưu).

**Ký số trong ứng dụng (lát 6b)**
- Lệnh ribbon "Ký số" (Bảo mật → Chữ ký). **Thứ được ký là tệp đã lưu**: tài liệu có thay đổi chưa lưu (hay tệp trên đĩa đã đổi/mất) → hỏi "Lưu rồi ký" / Hủy; tài liệu có bảo vệ hoặc chỉ xem → từ chối (gỡ mật khẩu trước). Tài liệu chưa đổi thì ký đúng tệp đó → **chữ ký đã có giữ hiệu lực** (ký nhiều người).
- Hộp thoại (`SignDialogViewModel`): chứng thư từ kho Windows `CurrentUser\My` (có khóa riêng, còn hạn, cho phép ký; thẻ/USB token: PIN do driver/Windows hỏi lúc ký) hoặc tệp .pfx/.p12 (`X509CertificateLoader.LoadPkcs12FromFile`, `EphemeralKeySet` — khóa chỉ trong bộ nhớ; mật khẩu chuyển từ PasswordBox ở code-behind, không binding, xóa khi đóng cửa sổ); vị trí: các **trường chữ ký trống** của tệp (đứng đầu, đọc bằng `FormReader`; trường đã ký có `Value = FormField.Signed`) / "Kéo khung trên trang" / chữ ký ẩn; hiển thị: ảnh chữ ký tay từ thư viện P5 (DPAPI), tên (CN), ngày giờ, lý do, nơi ký (liên hệ chỉ ghi vào `/ContactInfo`); không chọn gì để hiện và không có ảnh thì vẫn hiện tên; dấu thời gian bật/tắt + địa chỉ TSA (mặc định `http://timestamp.digicert.com`), nhớ trong Cài đặt (`AppSettings.TimestampServer/UseTimestamp`).
- "Kéo khung trên trang" (`SignaturePlacementViewModel`, lớp `SignaturePlacementLayer`): sau hộp thoại, viewer vào chế độ đặt khung một lần (thanh gợi ý + Hủy); kéo = khung (cắt theo trang), bấm = khung 180×60 pt quanh điểm, thẳng theo trang đang hiển thị (trang xoay 90/270 thì đổi chiều), nằm trong trang; Esc, bật chế độ khác hay đóng tab = bỏ. Khung trong không gian PDF của trang nguồn = không gian của trang đã lưu (như vùng redaction); tài liệu đổi trong lúc đặt → báo lưu rồi ký lại.
- Ghi: hỏi nơi lưu (mặc định "{tên} (đã ký).pdf" cạnh tệp), tệp đang mở ở tab khác thì từ chối; ký nền ở thanh trạng thái (hủy được) bằng `SignOperation.SignFileAsync` (tạm → thay); TSA lỗi → hỏi "Ký không có dấu thời gian" / Hủy; lỗi khác giải thích theo `SigningFailure` (khóa riêng không dùng được: thẻ chưa cắm, sai PIN…). Xong: mở bản đã ký ở tab mới, hoặc **thay tab** (cùng vị trí, cùng trang) khi ghi đè chính tệp của tài liệu. Trường mới tên "Chữ ký n" (không trùng tên trường nào). Không ghi nhật ký tên người ký, lý do, nơi ký hay mật khẩu.

**Nghiệm thu (đến lát hiện tại)**
- Tệp sau mã hóa không mở được khi thiếu/sai mật khẩu, mở được bằng mật khẩu đúng ra cùng chữ — kiểm bằng **cả PDFium và PdfPig**; R6/R4 đúng thuật toán; đúng quyền đã chọn, mật khẩu chủ mở mọi quyền; mật khẩu tiếng Việt; gỡ bảo vệ chỉ với mật khẩu chủ; lưu tài liệu đã mở khóa ra tệp vẫn mã hóa với mật khẩu đã biết/mới; `ProtectionSetting` khứ hồi qua manifest không có mật khẩu.
- Ký số: chữ ký ẩn/hiển thị trên `pii.pdf` được PDFium tìm thấy và kiểm đúng; tệp gốc là tiền tố byte-cho-byte của tệp đã ký; người ký thứ hai → **cả hai chữ ký hợp lệ** (chữ ký đầu "không phủ toàn tệp"); sửa 1 byte → báo hỏng; ký vào trường trống có sẵn; trang xoay; tệp dùng xref stream; timestamp (TSA giả lập RFC 3161 trong test) đọc lại đúng giờ; kiểm chéo bằng poppler `pdfsig` (ngoài bộ test): cả hai chữ ký "Signature is Valid".
- Làm sạch: tài liệu dựng sẵn có đủ loại (Info + XMP, 3 script, tệp đính kèm + chú thích đính kèm, nút gửi biểu mẫu + liên kết chạy chương trình, liên kết web, lớp ẩn, chữ ngoài CropBox, thumbnail, PieceInfo, một bản cập nhật gia tăng, rác sau %%EOF) → mọi thứ bị gỡ và đếm đúng, PDFium không còn đọc được tiêu đề/tác giả/ngày/phần mềm, chỉ còn một %%EOF, chuỗi của bản cũ không còn trong tệp, chữ hiển thị giữ nguyên; lớp ẩn làm đổi màu phần sau → trang được giữ và báo.
- Redaction trên `pii.pdf` (và trang xoay/cắt), cả hai chế độ: chữ bị xóa không còn trích được bằng PdfPig lẫn PDFium (kể cả trong byte của tệp), điểm ảnh trong vùng đúng màu ô, ngoài vùng không đổi; chú thích/bookmark/metadata/cây thẻ chứa nội dung bị gỡ; vùng đánh dấu trên viewer đi theo trang, áp dụng = một bước hoàn tác. Chế độ giữ vector: chữ cạnh vùng còn nguyên ("Số CCCD:"), `vanban-vi.pdf`/`basic-vi.pdf` không trang nào phải chuyển ảnh; đường kẻ, dải màu, ảnh chỉ mất phần dưới vùng (điểm ảnh trong dữ liệu ảnh bị tô); chữ trong form XObject dưới vùng → trang đó chuyển ảnh.

### Hệ tọa độ — nguồn bug số một

PDF dùng đơn vị point (1/72 inch), gốc ở góc **dưới-trái**, trang có thể có `/Rotate` 90/180/270 và CropBox không bắt đầu từ (0,0). WPF dùng DIP (1/96 inch), gốc ở góc **trên-trái**. Mọi chuyển đổi bắt buộc đi qua một lớp duy nhất, có unit test cho cả 4 góc xoay × CropBox lệch gốc × nhiều mức zoom.

```csharp
public sealed class PageCoordinateMapper
{
    public PageCoordinateMapper(PdfRect box, int rotationDegrees, double scale) { /* ... */ } // scale = view units per point
    public ViewPoint PdfToScreen(PdfPoint p);    // vẽ highlight, kết quả tìm kiếm, đích bookmark
    public PdfPoint  ScreenToPdf(ViewPoint p);   // người dùng click/kéo để thêm đối tượng
    public ViewRect  PdfRectToScreen(PdfRect r);
    public PdfRect   ScreenRectToPdf(ViewRect r);
}
```

### Tiếng Việt

- Font chuẩn của PDF (Helvetica, Times… — nhóm Standard 14) **không có dấu tiếng Việt**. Luôn nhúng font Unicode đóng gói kèm app (Noto Sans, Noto Serif, Be Vietnam Pro — license OFL) dạng subset.
- PDFsharp cần `IFontResolver` trỏ tới font đóng gói; PDFium nạp font từ dữ liệu TTF khi tạo text object.
- OCR dùng `tessdata` cho `vie` + `eng`.
- Text trích từ PDF có thể ở dạng tổ hợp (NFD). Chuẩn hóa **NFC** trước khi tìm kiếm, so sánh, copy; hỗ trợ tìm kiếm bỏ dấu ("hoa" khớp "hòa", "hóa").



### Phụ lục A — Bộ file mẫu (samples/)

Bộ "đề thi" để agent tự kiểm tra. Có thể nhờ agent viết project `tests/SampleGenerator` (dùng PDFsharp) để sinh tự động phần lớn các file này.

| Tên file | Đặc điểm | Dùng để test |
|---|---|---|
| `basic-vi.pdf` | Văn bản tiếng Việt xuất từ Word, có bookmark nhiều cấp | Chọn chữ, tìm kiếm, sửa chữ, bookmark |
| `big-1000.pdf` | 1.000 trang, có ảnh | Hiệu năng, bộ nhớ, ảo hóa |
| `scan-vi.pdf` | Hiện có (P4): 18 trang ảnh xám 90 DPI giả lập bản scan của `vanban-vi.pdf` — trang nghiêng, trang lộn ngược, tờ trắng phân cách (một tờ có chữ thấm mờ); bản 300 DPI dày hơn bổ sung ở P10 | Tách theo trang đánh dấu / trang trắng; OCR, nén, công cụ scan |
| `vanban-vi.pdf` | 14 trang: 5 văn bản hành chính giả liền nhau, trang đầu mỗi văn bản có quốc hiệu và "Số: …", bookmark cấp 1 có dấu và "/" | Tách theo văn bản (regex), theo bookmark tiếng Việt, đặt tên tệp |
| `encrypted.pdf` | Mật khẩu mở `test123`, giới hạn quyền in/copy | Mật khẩu, quyền |
| `form.pdf` | AcroForm đủ loại field | Điền, thiết kế, flatten form |
| `signed.pdf` | Đã ký số bằng chứng thư test | Incremental save, xác minh chữ ký |
| `rotated-cropbox.pdf` | Trang /Rotate 90 và 270, CropBox lệch gốc | Hệ tọa độ, overlay, annotation |
| `links-annots.pdf` | Link nội bộ (đích trực tiếp, đích có tên, action GoTo), link web, ghi chú, highlight, bookmark, page label | Giữ liên kết/chú thích khi sắp xếp, đổi khổ trang |
| `broken.pdf` | Bị cắt cụt đuôi file (hỏng xref) | Sửa file, chống crash |
| `tables.pdf` | Bảng có kẻ và không kẻ, tiếng Việt | Trích bảng ra Excel |
| `contract-v1.pdf`, `contract-v2.pdf` | Bản 2 chèn 1 trang và sửa 3 chỗ | So sánh tài liệu |
| `annotated.pdf` | Annotation do Acrobat tạo | Đọc/sửa annotation có sẵn |
| `pii.pdf` | Dữ liệu GIẢ: số CCCD, SĐT, email, số tài khoản | Redaction theo mẫu |

> [!WARNING]
> **Không dùng tài liệu thật để test**
> Đừng đưa tài liệu thật chứa thông tin cá nhân, hợp đồng khách hàng hay hồ sơ nội bộ vào `samples/` (thư mục này được commit lên git). Luôn dùng dữ liệu giả.

### Phụ lục B — Tám sai lầm kinh điển khi làm app PDF

1. **Bỏ qua /Rotate và CropBox** → highlight, chữ chèn, ô redaction lệch khỏi vị trí người dùng thấy.
2. **Gọi PDFium từ nhiều luồng** → crash ngẫu nhiên, rất khó tái hiện. Luôn đi qua `PdfiumGate`.
3. **Dùng font chuẩn Standard 14** → mất dấu tiếng Việt hoặc ra ô vuông.
4. **"Redact" bằng cách vẽ ô đen đè lên** → chữ vẫn nằm nguyên bên dưới, copy ra được — lỗi kinh điển từng làm lộ nhiều tài liệu mật. Phải xóa thật và hậu kiểm.
5. **Lưu không incremental file đã ký** → chữ ký số mất hiệu lực mà người dùng không biết. (Đã xử lý ở P8 lát 8a cho trường hợp chỉ thêm chú thích; các thay đổi khác vẫn cảnh báo trước khi lưu.)
6. **Nén lại JPEG nhiều lần** → ảnh xấu dần sau mỗi lần lưu. Luôn nén từ dữ liệu gốc, JPEG không cần thì giữ nguyên.
7. **Cache bitmap không giới hạn** → file lớn ngốn vài GB RAM rồi crash. Với WPF còn phải tính bản texture mà render phần cứng giữ cho mỗi bitmap đã vẽ (xem mục Trình xem).
8. **So sánh/tìm kiếm không chuẩn hóa Unicode** → hai chuỗi trông giống hệt bị coi là khác nhau (NFC và NFD).

### Phụ lục C — Nguồn tham khảo chính thức

- Chính sách hỗ trợ .NET: dotnet.microsoft.com/platform/support/policy
- Tài liệu PDFsharp (mã hóa, chữ ký số, PDF/A): docs.pdfsharp.net
- PDFium binaries cho mọi nền tảng: github.com/bblanchon/pdfium-binaries
- PDFiumCore (binding .NET cho PDFium): github.com/Dtronix/PDFiumCore
- Header API PDFium (tham chiếu tên hàm chính xác): pdfium.googlesource.com/pdfium (thư mục public/)
- PdfPig: github.com/UglyToad/PdfPig
- Tesseract OCR: tesseract-ocr.github.io
- qpdf manual: qpdf.readthedocs.io
- Velopack: velopack.io

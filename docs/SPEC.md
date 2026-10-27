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
5. **Lưu không incremental file đã ký** → chữ ký số mất hiệu lực mà người dùng không biết.
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

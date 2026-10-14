# PDF_Pro — Đặc tả kỹ thuật (SPEC)

> Nguồn sự thật của dự án. Cập nhật lần cuối: 14/10/2026. Khi thiết kế thay đổi, cập nhật file này trước rồi mới code.

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
| Gộp & tách | Gộp nhiều file giữ bookmark + mục lục tự động; tách theo khoảng, mỗi N trang, theo bookmark, theo dung lượng, theo trang trắng phân cách, theo văn bản (regex); trộn trang lẻ/chẵn; N-up; booklet; poster | Pro | P4 |
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
| Cấu trúc & overlay | PDFsharp 6.2+ và PDFsharp.Cryptography | MIT | Ghép/tách, vẽ lên trang, AES-256, ký số CMS, PDF/A |
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
├─ CLAUDE.md                     // chứa dòng @AGENTS.md (cho Claude Code)
├─ Directory.Build.props         // net10.0, Nullable, TreatWarningsAsErrors
├─ Directory.Packages.props      // version NuGet quản lý tập trung
├─ global.json                   // ghim .NET SDK 10
├─ PdfPro.slnx
├─ THIRD_PARTY_NOTICES.md        // license mọi thư viện, font, tessdata
├─ docs/      SPEC.md · PROMPTS.md · adr/
├─ samples/                      // PDF mẫu để test (dữ liệu giả)
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
   ├─ PdfPro.App.Tests/          // test ViewModel, bảng chuỗi vi/en
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
public enum OperationKind { PlanOnly, Content }

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
- Cache render LRU theo ngân sách byte (mặc định 512 MB) + cache thumbnail trên đĩa theo (revision, trang, kích thước).
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

### Hệ tọa độ — nguồn bug số một

PDF dùng đơn vị point (1/72 inch), gốc ở góc **dưới-trái**, trang có thể có `/Rotate` 90/180/270 và CropBox không bắt đầu từ (0,0). WPF dùng DIP (1/96 inch), gốc ở góc **trên-trái**. Mọi chuyển đổi bắt buộc đi qua một lớp duy nhất, có unit test cho cả 4 góc xoay × CropBox lệch gốc × nhiều mức zoom.

```csharp
public sealed class PageCoordinateMapper
{
    public PageCoordinateMapper(PdfRect cropBox, int rotationDegrees, double zoom) { /* ... */ }
    public Point    PdfToScreen(PdfPoint p);     // vẽ highlight, kết quả tìm kiếm
    public PdfPoint ScreenToPdf(Point p);        // người dùng click/kéo để thêm đối tượng
    public Rect     PdfRectToScreen(PdfRect r);
    public PdfRect  ScreenRectToPdf(Rect r);
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
| `scan-vi.pdf` | 20–50 trang scan 300 DPI, có trang nghiêng, lộn ngược, trang trắng | OCR, nén, công cụ scan |
| `encrypted.pdf` | Mật khẩu mở `test123`, giới hạn quyền in/copy | Mật khẩu, quyền |
| `form.pdf` | AcroForm đủ loại field | Điền, thiết kế, flatten form |
| `signed.pdf` | Đã ký số bằng chứng thư test | Incremental save, xác minh chữ ký |
| `rotated-cropbox.pdf` | Trang /Rotate 90 và 270, CropBox lệch gốc | Hệ tọa độ, overlay, annotation |
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
7. **Cache bitmap không giới hạn** → file lớn ngốn vài GB RAM rồi crash.
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

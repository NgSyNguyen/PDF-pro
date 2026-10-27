# PDF_Pro — Nhật ký tiến độ & bàn giao

> Tệp này được `CLAUDE.md` nạp tự động: mọi phiên Claude Code (trên máy hay trên cloud) đọc nó ngay khi mở repo.
> **Cập nhật tệp này ở cuối mỗi lát** (trạng thái, commit, quyết định, việc tiếp theo) trong cùng commit với lát đó.
> Nguồn sự thật về thiết kế vẫn là `docs/SPEC.md`; lộ trình là `docs/PROMPTS.md`; luật là `AGENTS.md`.

Cập nhật lần cuối: 27/10/2026 — Phase 6, sau lát 9a.

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
| CI gần nhất | `88dd914` (P6 lát 8b): xanh. Mốc đếm: `292e6a3` có 1.740 test, 1.726 đạt, 14 bỏ qua (UI test FlaUI tự bỏ qua khi không đặt `PDFPRO_UI_TESTS=1`) |

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
| **P6 Chú thích & tìm kiếm** | **Đang làm** — xem bảng lát Phase 6 |
| P7–P13 | Chưa bắt đầu (`PdfPro.Ocr` trống, `PdfPro.Cli` mới có `--version`) |

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
| 9 | Hộp thoại Bates nhiều tệp (`stamp.bates`): danh sách tệp, số chạy nối tiếp, nhãn, thư mục ra, chạy nền | `4ebc005` | Xong |
| 10 | Mục Phase 5 trong SPEC + `Phase5AcceptanceTests` (FlaUI: hình mờ một bước hoàn tác + gỡ lại; Bates 3 tệp) | `493f280` | Xong — **chưa chạy tay trên Windows** |

Phase 5 xong về code. Còn việc của chủ dự án: chạy `Phase5AcceptanceTests` trên Windows (`PDFPRO_UI_TESTS=1`), ghi số đo vào SPEC mục Phase 5, kiểm tay chữ tiếng Việt trong Acrobat Reader/Chrome.

### Phase 6 — các lát (kế hoạch do agent tự quyết, 27/10/2026)

| Lát | Nội dung | Commit | Trạng thái |
|---|---|---|---|
| 1 | Lớp ký tự của trang: PDFium (`PdfiumTextPage.GetLayer`, `IPdfDocument.GetPageTextLayerAsync`) + Core `PageText` (dòng, từ, ký tự tại điểm, caret khi kéo, hình chữ nhật theo dòng, chép chữ NFC nối dòng / sửa gạch nối / giữ đoạn) | `66c9fc5` | Xong |
| 2 | Tìm kiếm tiếng Việt (Core `TextSearch`): NFC = NFD, hoa/thường, dấu Tự động/Bỏ qua/Khớp (cả đ/Đ), toàn từ, regex (timeout 2 s), cụm từ khớp qua dòng; kết quả là khoảng ký tự; đoạn trích có ngữ cảnh | `5accbd9` | Xong |
| 3 | Chọn chữ trên viewer: `TextLayerCache` (LRU 256 trang, dùng chung với tìm kiếm), `TextSelectionViewModel` (bấm/kéo, đúp = từ, ba lần = dòng, kéo theo từ/dòng, xuyên trang, Ctrl+A cả tài liệu, Esc), `TextSelectionLayer` (chỉ vẽ), con trỏ chữ I, Ctrl+C | `7944a57` | Xong, CI xanh |
| 4 | Tìm kiếm: `SearchViewModel` (chạy nền qua `TextLayerCache`, kết quả đổ dần kèm ngữ cảnh, kết quả đang chọn được chọn chữ + cuộn tới, F3/Shift+F3 vòng quanh, Dừng, tự tìm lại khi đổi tùy chọn hoặc đổi trang, đếm trang ảnh → gợi ý OCR); sidebar thêm mục "Tìm kiếm"; `TextHighlightLayer` (thay `TextSelectionLayer`) vẽ kết quả vàng/cam + vùng chọn; Ctrl+F, F3, Shift+F3, nút ribbon "Tìm" | `de2ed54` | Xong, CI xanh |
| 5 | Core `Annotations/AnnotationItems.cs` (markup 4 loại, ghi chú, hộp chữ có đường chỉ, mực, chữ nhật/elip, đường/mũi tên, đa giác/đường gấp khúc, con dấu chữ/ảnh; trả lời, trạng thái duyệt; JSON đa hình; `Validate`, `MoveBy`), `PdfDate.Format`; Operations `Annotating/AnnotationWriter` (PDFsharp tầng dictionary, `/AP` cho mọi loại, highlight nhân màu, chữ tiếng Việt nhúng font, thẳng theo góc xoay trang; trả lời/trạng thái = `/Text` có `/IRT`) | `6d981a2` | Xong, CI xanh |
| 6 | `PageRef.Annotations` (đi theo trang khi di chuyển/xoay/cắt/nhân bản; là nội dung: `HasSameContent`, `IsIdentity`), `PagePlan.WithAnnotations`; thao tác plan `annotate.add/update/delete` (`Annotating/AnnotateOperations.cs`, 1 bước hoàn tác, làm tài liệu "chưa lưu"); manifest `PageDto.Annotations`; `PdfSharpPlanMaterializer` ghi bằng `AnnotationWriter`; mô tả lịch sử "Thêm/Sửa/Xóa n chú thích" | `f976c18` | Xong |
| 7a | Công cụ chú thích trên viewer: `ViewModels/Annotating/AnnotationToolsViewModel` (tô sáng/gạch chân/gạch ngang/lượn sóng vùng chọn — có vùng chọn thì đánh dấu ngay, chưa có thì bật công cụ; vùng chọn nhiều trang = 1 bước; ghi chú: bấm đặt, nhả chuột mở ô bình luận; bấm chọn, kéo di chuyển, Delete xóa, Esc; đúp ghi chú mở bình luận), `AddAnnotationsParams` nhiều trang (`PageAnnotations`, `AddAnnotationsParams.On`), Core `AnnotationHitTest`, `Services/Annotating/AnnotationDrawing` (vẽ mọi loại như bản ghi PDF), `Controls/AnnotationLayer`, thanh "Đang dùng: … — Esc để tắt", Popup bình luận; ribbon `annotate.highlight/underline/strikeout/squiggly/note`; cài đặt `AnnotationAuthor` (trống = tên Windows); in ấn vẽ chú thích của phiên; `PagePlan.HasSamePages` (chú thích không xóa vùng chọn chữ, không chạy lại tìm kiếm) | `d8e3b01` | Xong, CI xanh |
| 7b | `AnnotationToolsViewModel.Drawing.cs`: vẽ tay (mỗi lần kéo = 1 nét, công cụ giữ nguyên), chữ nhật/elip, đường/mũi tên (kéo; bấm = cỡ mặc định), đa giác/đường gấp khúc (bấm thêm điểm, bấm đúp xong, Esc bỏ), hộp chữ (kéo khung → gõ trong Popup → thêm khi có chữ, tự cao theo chữ), con dấu (ĐÃ DUYỆT/ĐÃ XEM/… + ngày); xem trước khi vẽ; màu (ô màu đổi màu chú thích đang chọn hoặc màu vẽ tiếp), nét; thanh công cụ đầy đủ (công cụ, màu, nét, chữ con dấu, Bình luận…, Xóa, Xong); ô "Tên tác giả chú thích" trong Cài đặt; ribbon `annotate.textBox/ink/shapes/stamp` | `183338f` | Xong |
| 8a | Đọc chú thích có sẵn trong tệp bằng PDFium: Core `Annotations/FileAnnotation.cs` (`FileAnnotationKind` theo số của PDFium, `IsComment`, `IsReply`, `IsHidden`), `PdfiumPage.GetAnnotations()` (NM, T, Contents, M, IRT → chỉ số, State/StateModel, F, QuadPoints), `AnnotationSafeHandle` (đóng cả chú thích liên kết), `IPdfDocument.GetPageAnnotationsAsync` | `be561a9` | Xong, CI xanh |
| 8b | Panel "Bình luận" (sidebar, mục thứ 4): `ViewModels/Annotating/CommentsViewModel.cs` (chú thích của phiên + có sẵn trong tệp, theo trang rồi trên→dưới như hiển thị; trả lời/trạng thái của tệp gom theo `/IRT`; lọc loại/tác giả/trạng thái/chữ (bỏ dấu); chọn → cuộn tới + chọn chú thích của phiên; trả lời, Chấp nhận/Từ chối/Hoàn thành, xóa = 1 bước hoàn tác; chỉ đọc khi panel hiện, đọc lại khi trang đổi); Core `CommentExport` (Markdown theo trang; CSV 1 dòng/bình luận + 1 dòng/trả lời, chống công thức, BOM UTF-8); `IFileDialogService.PickCommentExportPath` | `88dd914` | Xong, CI xanh |
| 8c | Sửa chú thích **có sẵn trong tệp** = thao tác nội dung `annotate.editFile` (`Annotating/EditFileAnnotationsOperation.cs`: trả lời, trạng thái, sửa nội dung, xóa cả luồng `/IRT` + `/Popup`; tìm chú thích theo chỉ số + loại + `/NM` hoặc `/Rect` ±0,5, không thấy thì từ chối chứ không sửa nhầm; `AnnotationWriter.AddToThread` dùng chung); panel Bình luận gửi `ContentChangeRequest` (cửa sổ chạy nền → revision mới, 1 bước hoàn tác; bị chặn khi trình chỉnh sửa còn đối tượng chờ áp dụng); tên lịch sử "Trả lời/Đặt trạng thái/Sửa/Xóa bình luận" | `e7420d8` | Xong |
| 9a | `annotate.flatten` (`Annotating/FlattenAnnotationsOperation.cs`): vẽ `/AP /N` (hoặc trạng thái `/AS`) của chú thích bình luận vào nội dung trang qua `PageStamper.Place` (BBox×Matrix khớp `/Rect`, 12.5.5; không nhân `/CA` vì `/AP` đã có độ mờ, như PDFium hiển thị), gỡ chúng cùng luồng `/IRT` + `/Popup` (`AnnotationThreads`); giữ liên kết, trường biểu mẫu, Redact, chú thích không có `/AP`; chú thích ẩn bị gỡ không vẽ; `Count(page)`. App: nhóm ribbon "Duyệt" (`annotate.comments` mở panel Bình luận, `annotate.flatten` hỏi "Cả tài liệu" / "Chỉ trang n" / "Chỉ n trang đã chọn" → cửa sổ chạy nền) | commit `feat: flatten annotations…` | Xong, chờ CI |
| 9b | Mục SPEC Phase 6, `Phase6AcceptanceTests` (FlaUI) | — | Tiếp theo |

**Quyết định Phase 6 (sẽ đưa vào SPEC mục Phase 6):**
- PDFium 157 có API annotation nhưng **không ghi được** `/L` (đường), `/Vertices` (đa giác), `/IRT` (trả lời); PDFsharp 6.2.4 **không** lưu incremental. → Annotation được ghi bằng **PDFsharp ở tầng dictionary** (đủ loại, `/AP` sinh bằng XGraphics, trả lời có `/IRT`), giữ trên `PageRef` như dấu tách trang (thêm/sửa/hoàn tác tức thì, chỉ ghi ra PDF khi lưu/vật chất hóa). Annotation có sẵn trong tệp: sửa/xóa là thao tác nội dung (revision).
- **Lưu incremental để không phá chữ ký số dời sang P8** (P8 phải viết bộ ghi incremental cho ký nhiều người; còn cần `signed.pdf` và bộ xác minh để kiểm chứng). Tới lúc đó lưu tệp đã ký vẫn cảnh báo như P3.
- **Chú thích của phiên** (`PageRef.Annotations`) nằm trong không gian PDF của trang nguồn (xoay/cắt của plan không dời chúng; hiển thị bằng mapper của `MaterializedGeometry`). Mọi lần vật chất hóa (lưu, xuất, xem trước đóng dấu, đầu vào của thao tác nội dung) đều ghi chúng vào tệp → **sau một thao tác nội dung** (revision mới, plan identity) chúng trở thành chú thích có sẵn trong tệp; sửa/xóa chúng từ đó là việc của lát 8 (thao tác nội dung). Ảnh của con dấu ảnh phải nằm trong thư mục phiên (assets) để khôi phục được.
- **Hiển thị chú thích của phiên:** ảnh trang do PDFium vẽ từ tệp nguồn (chưa có chúng) → `AnnotationLayer` vẽ đè bằng WPF (`AnnotationDrawing`; highlight vẽ trong suốt 40% vì WPF không nhân màu). In ấn (`PdfPrintPaginator`) cũng vẽ đè như vậy. **Thumbnail chưa hiện chú thích của phiên** (rủi ro đã biết). Chuột: code-behind `DocumentView` gọi `Annotations.Press` trước, không trúng chú thích mới tới chọn chữ.
- Popup bình luận (`StaysOpen=False`, `Placement=MousePoint`) chỉ mở sau khi nhả chuột (`Release`) — mở ngay lúc nhấn thì lần nhả chuột đóng nó. Bấm ra ngoài = giữ nội dung đã gõ; Esc = bỏ; Ctrl+Enter = xong.
- `PageRef` là record: so sánh `Annotations` theo tham chiếu danh sách (bản đọc lại từ manifest là thể hiện mới). `HasSameContent` so từng chú thích theo `Equals` của record.
- `/AP` của chú thích: `/BBox` = `/Rect` trong không gian trang, ma trận đơn vị; nét vẽ viết thẳng content stream (số invariant); chữ (hộp chữ, con dấu) vẽ bằng `StampForms` rồi đặt bằng `FormToLocal.Then(new PageCoordinateMapper(Box, Rotation, 1).ScreenToPdfMatrix)`. FreeText theo Acrobat: `/C` = màu nền, màu viền trong `/DA`. Mảng `/Annots` luôn được chép thành mảng riêng của trang (có thể dùng chung giữa các trang).
- Namespace `PdfPro.Operations.Annotating` (không phải `Annotations`: đụng hàm `PdfGraph.Annotations(page)`).
- `PdfRect` có `[JsonConstructor]` và `[JsonIgnore]` cho Width/Height/IsEmpty/Size (trước đó deserialize ra toàn 0 vì là struct).
- Hộp ký tự dùng `FPDFText_GetLooseCharBox` (cao từ ascent tới descent của phông → các ký tự một dòng cùng đỉnh/đáy), trong không gian PDF của trang nguồn = không gian của trang sau khi vật chất hóa (map bằng `MaterializedGeometry` như overlay).

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
- **Chọn chữ:** chuột trên `DocumentViewPanel` (code-behind chỉ chuyển tiếp) → `TextSelectionViewModel` đổi điểm viewport → (trang, điểm PDF) bằng `DocumentLayout` + `PageCoordinateMapper` với hình học của `PageEditorViewModel.GeometryOf`; vùng chọn bị xóa khi plan đổi (vị trí theo chỉ số trang). Kéo sang trang chưa đọc chữ thì lần di chuột sau mới bắt được (trang được đọc nền). Chưa có tự cuộn khi kéo sát mép.
- `DocumentTabFactory` có tham số tùy chọn ở cuối (`screenFonts`, `screenImages`, `signatures`, `saver`): test dựng factory không truyền chúng thì tính năng tương ứng tắt — test của tính năng phải truyền vào (ví dụ `saver: _saver`).
- Test ribbon `Ribbon_OnlyImplementedCommandsAreAvailable` liệt kê đủ id lệnh đã có — thêm nút mới phải thêm id vào đó.
- PDFsharp **ghim 6.2.4** (6.5+ là AGPL, có test canh). Không thêm package mới nếu stack hiện có làm được.

## 7. Rủi ro / nợ đang mở

- Gỡ dấu trên tài liệu lớn: không có tiến độ khi quét.
- `PdfPro.Benchmarks`, `PdfPro.Worker` chưa tạo (để P13).

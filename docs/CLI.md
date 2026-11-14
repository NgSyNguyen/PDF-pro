# pdfpro — PDF_Pro từ dòng lệnh

`pdfpro` (dự án `PdfPro.Cli`) dùng **chung các thao tác** với ứng dụng (`IPdfOperation`): kết quả giống hệt khi bấm nút
trong app. Không phụ thuộc WPF, chạy được trên Windows và Linux.

- Kết quả (đường dẫn các tệp đã ghi, mỗi dòng một tệp — hoặc JSON với `--json`) ra **stdout**.
- Tiến độ và lỗi ra **stderr** (`--quiet` để tắt tiến độ).
- Không bao giờ ghi đè tệp vào (trừ `--in-place`), không ghi gì ra đích khi một bước lỗi; tệp tạm luôn được xóa.
- Ctrl+C dừng gọn (mã thoát 130); bấm lần hai thì thoát ngay.

## Mã thoát

| Mã | Ý nghĩa |
|---|---|
| 0 | Thành công |
| 1 | Thao tác thất bại (với một số hoặc tất cả tệp, vì nhiều lý do khác nhau) |
| 2 | Sai cú pháp, tham số không hợp lệ, workflow sai, thiếu mật khẩu/chứng thư cho một bước |
| 3 | Tệp vào không có, không đọc được hoặc hỏng |
| 4 | Tệp có mật khẩu: thiếu hoặc sai mật khẩu chủ |
| 5 | Chưa cài chương trình cần thiết (Tesseract, LibreOffice, Microsoft Word/WPS, qpdf) |
| 6 | Không ghi được kết quả |
| 70 | Lỗi không lường trước |
| 130 | Đã hủy (Ctrl+C) |

Khi mọi tệp lỗi **cùng một lý do**, mã thoát là mã của lý do đó (ví dụ 3); lỗi khác nhau thì 1.

## Tùy chọn chung (mọi lệnh)

| Tùy chọn | |
|---|---|
| `-o, --out <thư mục>` | Thư mục kết quả (mặc định: thư mục hiện tại). Với `merge` và `convert --to pdf` từ ảnh: tên tệp `.pdf`. |
| `--overwrite` | Ghi đè kết quả trùng tên (mặc định thêm " (2)"). Không bao giờ ghi đè tệp vào. |
| `--in-place` | Thay chính tệp vào bằng kết quả (ghi tệp tạm cạnh nó rồi thay). |
| `-r, --recursive` | Thư mục: tìm cả thư mục con (kết quả giữ cấu trúc thư mục con). |
| `--pattern <mẫu>` | Mẫu tên tệp trong thư mục, mặc định `*.pdf`; nhiều mẫu cách bằng `;`. Bỏ tệp ẩn/tạm và `_done`, `_error`. |
| `--parallel <n>` | Số tệp chạy cùng lúc (0 = nửa số nhân). |
| `--password <mk>` | Mật khẩu **chủ** của tệp vào có bảo vệ (hoặc biến môi trường `PDFPRO_PASSWORD`). |
| `--json` | Kết quả JSON trên stdout. |
| `-q, --quiet` | Không ghi tiến độ. |
| `--log <tệp.csv>` | Nhật ký lô (CSV UTF-8 có BOM, mở được bằng Excel). |
| `-p, --param tên=giá trị` | Tham số thêm cho thao tác (lặp lại được; xem `list-ops`). |

> **Mật khẩu trên dòng lệnh** có thể bị người dùng khác trên máy nhìn thấy (danh sách tiến trình, lịch sử shell). Dùng
> biến môi trường `PDFPRO_PASSWORD`, `PDFPRO_OWNER_PASSWORD`, `PDFPRO_USER_PASSWORD`, `PDFPRO_PFX_PASSWORD` hoặc tệp
> `--secrets` thay thế. pdfpro không bao giờ ghi mật khẩu vào log hay workflow.

## Lệnh

### Thông tin
```powershell
pdfpro info "hợp đồng.pdf"
pdfpro info "hợp đồng.pdf" --json          # JSON: pages, pageSizes, version, encrypted, permissions, signatures,
                                            # form, title, author…, links, bookmarks, attachments, fonts
pdfpro list-ops                             # mọi thao tác và tham số ([tên] = tùy chọn)
pdfpro list-ops --json
```

### Trang và tệp
```powershell
pdfpro merge a.pdf b.pdf c.pdf -o "hồ sơ.pdf" --toc        # mục lục, bookmark theo tệp (--no-bookmarks để bỏ)
pdfpro split "sổ tay.pdf" --ranges "1-3, 4-10, 11-" -o tach
pdfpro split "scan.pdf" --blank                            # tách theo tờ trắng phân cách
pdfpro split "báo cáo.pdf" --every 10   |   --bookmarks 1   |   --size 10
pdfpro rotate scan.pdf --degrees 90 --pages "1, 3-5"
```

### Tối ưu, chuyển đổi
```powershell
pdfpro compress *.pdf --preset ebook -o nho            # screen | ebook | print | archive; --dpi, --quality = tùy chỉnh
pdfpro convert bao-cao.pdf --to png --dpi 150          # png | jpeg | tiff
pdfpro convert van-ban.pdf --to markdown               # text | markdown
pdfpro convert ho-so.pdf --to pdfa                     # pdfa (2b) | pdfa1b
pdfpro convert ho-so.pdf --to grayscale                # thang xám | web (tối ưu xem trên web, cần qpdf)
pdfpro convert anh1.jpg anh2.png --to pdf -o "album.pdf"   # ảnh → một PDF (JPEG giữ nguyên byte)
pdfpro convert "biên bản.docx" --to pdf                # Office → PDF (Word: Microsoft Word/WPS nếu có, còn lại LibreOffice)
pdfpro ocr scans\ -r --lang vie+eng -o da-ocr          # --redo: làm lại lớp chữ cũ; --all-pages; --straighten
```

### Đóng dấu
```powershell
pdfpro watermark *.pdf --text "BẢN SAO" --opacity 0.2 --rotation 45
pdfpro stamp *.pdf --page-numbers "Trang {page}/{pages}"
pdfpro stamp *.pdf --header "CÔNG TY ABC" --footer "{filename} — {date:dd/MM/yyyy}"
pdfpro stamp ho-so\ --bates "HS-" --digits 6 --start 1   # số Bates chạy nối tiếp qua các tệp
```

### Bảo mật
```powershell
$env:PDFPRO_OWNER_PASSWORD = "..."; pdfpro encrypt *.pdf --user-password "..." --allow print,copy
pdfpro decrypt "mật.pdf" --password "<mật khẩu chủ>"
pdfpro redact ho-so\ -r --pattern cccd,phone,email,account,date --label "Đã xóa"   # --regex "...", --keep-text
$env:PDFPRO_PFX_PASSWORD = "..."; pdfpro sign *.pdf --pfx ky.pfx --visible last --reason "Phê duyệt" --tsa http://timestamp.digicert.com
```
- `redact` mặc định biến trang có vùng xóa thành ảnh (an toàn tuyệt đối); `--keep-text` giữ chữ và hình vẽ còn lại (nâng cao).
- `sign` mặc định chữ ký ẩn; `--visible first|last` đặt chữ ký ở góc dưới phải trang đầu/cuối; `--pades` ký chuẩn PAdES (`ETSI.CAdES.detached`, có `--tsa` là PAdES-B-T); `--ltv` thêm dữ liệu xác minh lâu dài (chứng thư + danh sách thu hồi tải qua mạng vào DSS); có `--tsa` thì đóng thêm dấu thời gian tài liệu sau DSS (với `--pades` là PAdES-B-LTA).

### Thao tác bất kỳ
```powershell
pdfpro run optimize.compress *.pdf -p preset=custom -p dpi=120 -p jpegQuality=70
pdfpro run stamp.headerFooter a.pdf --params '{ "topRight": "Mật", "style": { "size": 8 } }'
```
Tham số theo `list-ops`: số dạng invariant (`0.5`), bool `true/false/có/không`, enum theo tên, danh sách/record là JSON.

## Workflow

```powershell
pdfpro run-workflow digitize "D:\Scan" -r -o "D:\Số hóa" --log nhat-ky.csv   # mẫu: digitize | publish | print
pdfpro run-workflow "phát hành.pdfflow" *.pdf --secrets mat-khau.json
pdfpro run-workflow ky.pdfflow *.pdf --pfx ky.pfx --visible last
```
- Mẫu dựng sẵn: **digitize** = OCR (vie) → xóa trang trắng → nén eBook → PDF/A-2b; **publish** = hình mờ → số trang → làm
  sạch → đặt mật khẩu (AES-256, cho in + sao chép); **print** = khổ A4 → sổ gập.
- Tệp `.pdfflow` (JSON, xuất từ app hoặc viết tay) **không bao giờ chứa mật khẩu**. Bước cần mật khẩu lấy từ `--secrets`:
  ```json
  { "security.encrypt": { "security": { "ownerPassword": "…", "userPassword": "…" } } }
  ```
  (khóa là mã thao tác, hoặc số thứ tự bước tính từ 0). Bước ký số lấy chứng thư từ `--pfx`.
- Bước "đo" (xóa trang trắng, xoay đúng chiều, làm thẳng, xóa viền đen, xóa theo mẫu) đo lại trên **từng tệp**; không
  thấy gì thì tệp đi tiếp nguyên vẹn.
- Một tệp lỗi không dừng cả lô; tệp lỗi được nêu trên stderr và trong `--json`/`--log`.

## JSON

```json
{
  "succeeded": 49,
  "failed": 1,
  "cancelled": 0,
  "files": [
    { "input": "D:\\Scan\\a.pdf", "status": "done", "outputs": ["D:\\Số hóa\\a.pdf"], "error": null },
    { "input": "D:\\Scan\\hỏng.pdf", "status": "failed", "outputs": [], "error": "bước 1 (ocr.run): tệp hỏng hoặc không phải PDF đọc được" }
  ]
}
```
`status`: `done` | `failed` | `cancelled`. `error` là câu giải thích theo ngôn ngữ của máy (vi mặc định, en khi Windows tiếng Anh).

## Công cụ ngoài

pdfpro tìm Tesseract và qpdf trong thư mục `tools\` cạnh `pdfpro.exe` (cùng bộ cài với app; `tools\fetch-tools.ps1`), rồi
tới chỗ cài thường gặp và `PATH`; LibreOffice theo registry/Program Files/`PATH`. Thiếu công cụ thì lệnh cần nó trả mã 5.

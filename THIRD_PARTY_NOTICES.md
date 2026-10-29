# Third-party notices

PDF_Pro is built only on open-source components whose licenses allow commercial distribution.
AGPL/GPL and commercial libraries (iText, Ghostscript, MuPDF, FluentAssertions ≥ 8, Syncfusion, Aspose, Apryse…) are not allowed.
Before adding a package: state its name, license and reason, and add it below.

## Shipped with the application

| Component | Version | License | Used for |
|---|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) | 10.0 | MIT | Runtime |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | MVVM (ObservableObject, RelayCommand) |
| [WPF-UI](https://github.com/lepoco/wpfui) | 4.3.0 | MIT | Fluent design controls, Mica, light/dark themes |
| [gong-wpf-dragdrop](https://github.com/punker76/gong-wpf-dragdrop) | 4.0.0 | BSD-3-Clause | Dragging pages (and PDF files from Explorer) in the page organizer grid, with the insertion marker and auto-scroll |
| [Microsoft.Extensions.Hosting](https://github.com/dotnet/runtime) | 10.0.13 | MIT | Generic host, dependency injection, configuration |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | 10.0.13 | MIT | Logging abstractions |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://github.com/dotnet/runtime) | 10.0.13 | MIT | DI abstractions |
| [Serilog](https://github.com/serilog/serilog) | 4.4.0 | Apache-2.0 | Structured logging |
| [Serilog.Extensions.Hosting](https://github.com/serilog/serilog-extensions-hosting) | 10.0.0 | Apache-2.0 | Serilog integration with the generic host |
| [Serilog.Extensions.Logging](https://github.com/serilog/serilog-extensions-logging) | 10.0.0 | Apache-2.0 | `ILogger` bridge (dependency of Serilog.Extensions.Hosting; used directly before the host starts, for the single-instance channel) |
| [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | 7.0.0 | Apache-2.0 | Rolling log files |
| [Serilog.Sinks.Debug](https://github.com/serilog/serilog-sinks-debug) | 3.0.0 | Apache-2.0 | Debugger output |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | 2.0.13 | MIT | `pdfpro` command-line parsing |
| [PDFiumCore](https://github.com/Dtronix/PDFiumCore) | 157.0.8100 | Apache-2.0 | .NET bindings for PDFium |
| [bblanchon.PDFium.Win32](https://github.com/bblanchon/pdfium-binaries) (native `pdfium.dll`) | 157.0.8100 | Apache-2.0 (packaging) | Pre-built PDFium binaries (win-x64, win-arm64) |
| [PDFium](https://pdfium.googlesource.com/pdfium/) | chromium/8100 | BSD-3-Clause, Apache-2.0 | PDF rendering, text, document structure |
| [PDFsharp](https://github.com/empira/PDFsharp) | 6.2.4 | MIT | Writes page plans to PDF (page tree, rotation, CropBox, imported pages) in `PdfPro.Operations`; also generates `samples/`. **6.5.0+ is AGPL-3.0 — never upgrade beyond 6.2.x** (guarded by `LicenseGuardTests`). |
| [System.Security.Cryptography.Pkcs](https://github.com/dotnet/runtime) | 10.0.13 | MIT | Digital signatures (CMS, RFC 3161 timestamps) in PdfPro.Operations; also a dependency of PDFsharp (pinned for security fixes) |
| [PdfPig](https://github.com/UglyToad/PdfPig) | 0.1.16 | Apache-2.0 | Second, independent text extractor: in `PdfPro.Operations` for the mandatory check after a redaction (no text may remain under a removed area, Phase 8) and the last way of repairing a damaged file (lenient parsing, pages copied — Phase 9), and in `PdfPro.Operations.Tests` (text PDF_Pro writes must read back exactly). Shipped with the application inside its folder (`UglyToad.PdfPig*.dll`). |
| [Noto Sans](https://github.com/notofonts/latin-greek-cyrillic) Regular, Bold, Italic, Bold Italic (`assets/fonts/NotoSans`, from [notofonts.github.io](https://github.com/notofonts/notofonts.github.io)) | hinted TTF, 2026-10 | SIL OFL 1.1 (`assets/fonts/NotoSans/OFL.txt`) | Vietnamese text PDF_Pro writes into documents (merge table of contents, labels, added text, stamps), embedded as subsets; shipped inside `PdfPro.Operations.dll` |
| [Noto Serif](https://github.com/notofonts/latin-greek-cyrillic) Regular, Bold, Italic, Bold Italic (`assets/fonts/NotoSerif`, from [notofonts.github.io](https://github.com/notofonts/notofonts.github.io)) | hinted TTF, 2026-10 | SIL OFL 1.1 (`assets/fonts/NotoSerif/OFL.txt`) | Serif choice for text added to documents, embedded as subsets; inside `PdfPro.Operations.dll` |
| [Be Vietnam Pro](https://github.com/bettergui/BeVietnamPro) Regular, Bold, Italic, Bold Italic (`assets/fonts/BeVietnamPro`, from [google/fonts](https://github.com/google/fonts/tree/main/ofl/bevietnampro)) | TTF, 2026-10 | SIL OFL 1.1 (`assets/fonts/BeVietnamPro/OFL.txt`) | Vietnamese-designed sans for text added to documents, embedded as subsets; inside `PdfPro.Operations.dll` |
| [Charm](https://github.com/cadsondemak/charm) Regular, Bold (`assets/fonts/Charm`, from [google/fonts](https://github.com/google/fonts/tree/main/ofl/charm)) | TTF, 2026-10 | SIL OFL 1.1 (`assets/fonts/Charm/OFL.txt`) | Handwriting style for signatures typed by name (drawn into the signature picture); shipped as files in `Fonts` next to `PdfPro.exe`, with their OFL.txt |
| [qpdf](https://github.com/qpdf/qpdf) (`qpdf.exe` and its DLLs, fetched by `tools/fetch-tools.ps1` into `tools/qpdf`, shipped next to `PdfPro.exe` in `tools\qpdf` with its license files) | 12.2.0 (msvc64) | Apache-2.0 | Fast Web View (linearize), object streams, cross-reference recovery of damaged files; run as a separate process (`IExternalToolRunner`), never linked |
| [Dancing Script](https://github.com/impallari/DancingScript) Regular (`assets/fonts/DancingScript`) | OTF, 2026-10 | SIL OFL 1.1, Reserved Font Name "Dancing Script" (`assets/fonts/DancingScript/OFL.txt`); shipped unmodified | Handwriting style for signatures typed by name (drawn into the signature picture); shipped as files in `Fonts` next to `PdfPro.exe`, with their OFL.txt |

### Libraries bundled inside PDFium

| Component | License |
|---|---|
| FreeType | FreeType License (FTL) — *Portions of this software are copyright © The FreeType Project (www.freetype.org). All rights reserved.* |
| libjpeg-turbo | IJG License, BSD-3-Clause, zlib |
| libpng | libpng License |
| zlib | zlib License |
| Little CMS (lcms2) | MIT |
| OpenJPEG | BSD-2-Clause |
| Abseil | Apache-2.0 |

The complete license texts will be collected in this file before the first release (Phase 13).

## Development and test only (not shipped)

| Component | Version | License | Used for |
|---|---|---|---|
| [xunit.v3](https://github.com/xunit/xunit) | 4.0.2 | Apache-2.0 | Unit tests (Microsoft.Testing.Platform runner) |
| [Shouldly](https://github.com/shouldly/shouldly) | 4.3.0 | BSD-3-Clause | Test assertions |
| [NSubstitute](https://github.com/nsubstitute/NSubstitute) | 6.2.0 | BSD-3-Clause | Test doubles |
| [FlaUI.UIA3 / FlaUI.Core](https://github.com/FlaUI/FlaUI) | 5.0.0 | MIT | UI acceptance tests that drive the built app through UI Automation (`tests/PdfPro.App.UiTests`, run by hand) |
| [Interop.UIAutomationClient](https://github.com/Roemer/UIAutomation-Interop) | 10.19041.0 | MIT | UI Automation COM interop (dependency of FlaUI.UIA3) |
| Arial, Times New Roman (Windows fonts) | — | Monotype (embedding allowed) | Subsets embedded in the generated sample PDFs only |

Fonts installed on the user's computer are embedded (as subsets) into documents only when the font's own license
bits (OS/2 `fsType`) allow editable embedding; PDF_Pro never ships them.

## Planned (added in later phases, listed here once added)

SkiaSharp, OpenCvSharp4, Tesseract + tessdata,
DiffPlex, tabula-sharp, ClosedXML, Velopack, BenchmarkDotNet.

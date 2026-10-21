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
| [System.Security.Cryptography.Pkcs](https://github.com/dotnet/runtime) | 10.0.13 | MIT | Pinned transitive dependency of PDFsharp (security fixes) |

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
| Arial, Times New Roman (Windows fonts) | — | Monotype (embedding allowed) | Subsets embedded in the generated sample PDFs only |

## Planned (added in later phases, listed here once added)

PdfPig, SkiaSharp, OpenCvSharp4, Tesseract + tessdata, qpdf,
DiffPlex, tabula-sharp, ClosedXML, Velopack, BenchmarkDotNet, FlaUI,
fonts Noto Sans / Noto Serif / Be Vietnam Pro (SIL OFL 1.1).

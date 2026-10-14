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
| [Microsoft.Extensions.Hosting](https://github.com/dotnet/runtime) | 10.0.13 | MIT | Generic host, dependency injection, configuration |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | 10.0.13 | MIT | Logging abstractions |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://github.com/dotnet/runtime) | 10.0.13 | MIT | DI abstractions |
| [Serilog](https://github.com/serilog/serilog) | 4.4.0 | Apache-2.0 | Structured logging |
| [Serilog.Extensions.Hosting](https://github.com/serilog/serilog-extensions-hosting) | 10.0.0 | Apache-2.0 | Serilog integration with the generic host |
| [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | 7.0.0 | Apache-2.0 | Rolling log files |
| [Serilog.Sinks.Debug](https://github.com/serilog/serilog-sinks-debug) | 3.0.0 | Apache-2.0 | Debugger output |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | 2.0.13 | MIT | `pdfpro` command-line parsing |

## Development and test only (not shipped)

| Component | Version | License | Used for |
|---|---|---|---|
| [xunit.v3](https://github.com/xunit/xunit) | 4.0.2 | Apache-2.0 | Unit tests (Microsoft.Testing.Platform runner) |
| [Shouldly](https://github.com/shouldly/shouldly) | 4.3.0 | BSD-3-Clause | Test assertions |
| [NSubstitute](https://github.com/nsubstitute/NSubstitute) | 6.2.0 | BSD-3-Clause | Test doubles |

## Planned (added in later phases, listed here once added)

PDFiumCore + pdfium-binaries, PDFsharp, PdfPig, SkiaSharp, OpenCvSharp4, Tesseract + tessdata, qpdf,
DiffPlex, tabula-sharp, ClosedXML, gong-wpf-dragdrop, Velopack, BenchmarkDotNet, FlaUI,
fonts Noto Sans / Noto Serif / Be Vietnam Pro (SIL OFL 1.1).

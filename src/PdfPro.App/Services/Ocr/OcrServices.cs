using PdfPro.Core.Ocr;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Services.Ocr;

/// <summary>What the OCR command of a document tab uses besides the operation.</summary>
/// <param name="Engine">The OCR engine (Tesseract), installed or not.</param>
/// <param name="Settings">Where the languages and the number of pages read at once are remembered; null in tests that do not need it.</param>
public sealed record OcrServices(IOcrEngine Engine, ISettingsService? Settings = null);

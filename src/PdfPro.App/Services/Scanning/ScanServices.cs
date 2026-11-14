using PdfPro.Core.Ocr;
using PdfPro.Operations.Scanning;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Scanning;

/// <summary>What the scan tools of the ribbon use: PDFium to look at pages, the operations, and OCR to tell turned pages.</summary>
public sealed record ScanServices(
    PdfiumGate Gate,
    RemoveBlankPagesOperation RemoveBlankPages,
    AutoRotateOperation AutoRotate,
    DeskewOperation Deskew,
    CleanBordersOperation CleanBorders,
    IOcrEngine? Ocr = null);

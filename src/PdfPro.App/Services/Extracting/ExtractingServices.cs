using PdfPro.App.Services.Rendering;
using PdfPro.Operations.Extracting;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Extracting;

/// <summary>What the extraction commands of the ribbon use: the operations, and PDFium to show pages.</summary>
public sealed record ExtractingServices(ExtractTablesOperation Tables, IPdfDocumentLoader Loader, IPageImageRenderer Renderer, ExtractImagesOperation? Images = null);

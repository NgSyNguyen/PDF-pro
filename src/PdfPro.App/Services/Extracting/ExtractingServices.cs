using PdfPro.App.Services.Rendering;
using PdfPro.Operations.Extracting;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Extracting;

/// <summary>What the extraction commands of the ribbon use: the operations, and PDFium to show pages.</summary>
/// <param name="Runner">Runs the table finding in the worker when it is on (null: always in the app).</param>
public sealed record ExtractingServices(ExtractTablesOperation Tables, IPdfDocumentLoader Loader, IPageImageRenderer Renderer, ExtractImagesOperation? Images = null, Optimizing.IExportRunner? Runner = null);

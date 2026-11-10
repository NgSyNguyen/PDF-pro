using PdfPro.Operations.Optimizing;

namespace PdfPro.App.Services.Optimizing;

/// <summary>
/// The file tools the window runs on a document: Fast Web View, repairing damaged files, compressing, greyscale and
/// converting to and from other formats.
/// </summary>
/// <param name="Compress">"Nén" (null: the command is off).</param>
/// <param name="Previews">Renders a page before and after compression for the dialog (null: no preview).</param>
/// <param name="Grayscale">"Thang xám" (null: the command is off).</param>
/// <param name="ToImages">"PDF sang ảnh" (null: the command is off).</param>
/// <param name="FromImages">"Ảnh sang PDF" (null: the command is off).</param>
/// <param name="ToText">"PDF sang văn bản" (null: the command is off).</param>
/// <param name="OfficeToPdf">"Office sang PDF" (null: the command is off).</param>
/// <param name="LibreOffice">Finds LibreOffice for <paramref name="OfficeToPdf"/>.</param>
public sealed record OptimizingServices(
    LinearizeOperation Linearize,
    RepairOperation Repair,
    CompressOperation? Compress = null,
    ICompressPreviews? Previews = null,
    GrayscaleOperation? Grayscale = null,
    Operations.Converting.ToImagesOperation? ToImages = null,
    Operations.Converting.FromImagesOperation? FromImages = null,
    Operations.Converting.ToTextOperation? ToText = null,
    Operations.Converting.OfficeToPdfOperation? OfficeToPdf = null,
    Operations.Converting.ILibreOfficeLocator? LibreOffice = null);

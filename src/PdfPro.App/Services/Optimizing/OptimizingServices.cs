using PdfPro.Operations.Optimizing;

namespace PdfPro.App.Services.Optimizing;

/// <summary>
/// The file tools the window runs on a document: Fast Web View, repairing damaged files, compressing, greyscale and
/// converting to pictures.
/// </summary>
/// <param name="Compress">"Nén" (null: the command is off).</param>
/// <param name="Previews">Renders a page before and after compression for the dialog (null: no preview).</param>
/// <param name="Grayscale">"Thang xám" (null: the command is off).</param>
/// <param name="ToImages">"PDF sang ảnh" (null: the command is off).</param>
/// <param name="FromImages">"Ảnh sang PDF" (null: the command is off).</param>
public sealed record OptimizingServices(
    LinearizeOperation Linearize,
    RepairOperation Repair,
    CompressOperation? Compress = null,
    ICompressPreviews? Previews = null,
    GrayscaleOperation? Grayscale = null,
    Operations.Converting.ToImagesOperation? ToImages = null,
    Operations.Converting.FromImagesOperation? FromImages = null);

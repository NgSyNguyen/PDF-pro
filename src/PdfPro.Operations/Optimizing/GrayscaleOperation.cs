using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>How "Thang xám" makes a document grey (PROMPTS P9 B: the trade-off is the user's).</summary>
public enum GrayscaleMode
{
    /// <summary>Colours are made grey where they are written: text stays text, drawings stay sharp, the file stays small.
    /// Pages with something that cannot be converted become pictures of themselves.</summary>
    Convert,

    /// <summary>Every page becomes a grey picture of itself: certainly grey, but text can no longer be selected or found.</summary>
    Raster,
}

/// <param name="Mode">Converting colours (default) or pictures of the pages.</param>
/// <param name="RasterDpi">Resolution of the pages made pictures.</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2).pdf").</param>
public sealed record GrayscaleParams(
    GrayscaleMode Mode = GrayscaleMode.Convert,
    [property: Range(72, 600)] int RasterDpi = 300,
    bool Overwrite = false);

/// <summary>What making a document grey did.</summary>
/// <param name="PageCount">Pages of the document.</param>
/// <param name="ImagesConverted">Images (and inline images) whose colours were made grey.</param>
/// <param name="PicturePages">Pages (0-based) that are now pictures of themselves: all of them in the raster mode.</param>
/// <param name="ColoredPages">Pages that still show colour after everything (none is expected).</param>
public sealed record GrayscaleReport(
    long OriginalSize,
    long OutputSize,
    int PageCount,
    int ImagesConverted,
    IReadOnlyList<int> PicturePages,
    IReadOnlyList<int> ColoredPages);

/// <summary>
/// optimize.grayscale (<see cref="OperationKind.Export"/>, "Thang xám"): a grey copy of a file (SPEC Phase 9). In the
/// convert mode <see cref="GrayscaleConverter"/> rewrites the colours, then PDFium renders every page to check that no
/// colour is left; pages it could not convert or that still show colour are rendered grey from the original and replace
/// themselves (their annotations stay; if one of those still shows colour, it is drawn into the picture and removed).
/// </summary>
public sealed class GrayscaleOperation(PdfiumGate gate) : PdfOperation<GrayscaleParams>, IExportOperation
{
    public const string OperationId = "optimize.grayscale";

    Type IExportOperation.ResultType => typeof(GrayscaleReport);

    async Task<object> IExportOperation.ExportAsync(ExportCall call, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        return await WriteAsync(call.Input, call.Output, (GrayscaleParams)parameters, progress, cancellationToken).ConfigureAwait(false);
    }

    public const string Stage = "Stage_Grayscale";

    /// <summary>Pixels per point of the colour check (36 DPI).</summary>
    private const double CheckScale = 0.5;

    /// <summary>A pixel whose channels differ by more than this is coloured (rendering rounds a little).</summary>
    private const int ColorTolerance = 6;

    /// <summary>Above this many pixels a page picture is rendered at a lower resolution.</summary>
    private const long MaxPixels = 40_000_000;

    private const string PictureName = "/PdfProGray";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Writes a grey copy of <paramref name="input"/> to <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="InvalidDataException">The file cannot be read (damaged, or protected by a password).</exception>
    public Task<GrayscaleReport> WriteAsync(string input, string output, GrayscaleParams parameters, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Check(parameters);
        return OptimizedFiles.WriteAsync(
            output,
            async (temporary, token) =>
            {
                var original = await File.ReadAllBytesAsync(input, token).ConfigureAwait(false);
                var (gray, report) = await ConvertAsync(original, parameters, progress, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(temporary, gray, token).ConfigureAwait(false);
                return report;
            },
            cancellationToken);
    }

    /// <summary>The grey document and what was done.</summary>
    internal async Task<(byte[] Data, GrayscaleReport Report)> ConvertAsync(byte[] original, GrayscaleParams parameters, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var pageCount = await Task.Run(() => PageCount(original), cancellationToken).ConfigureAwait(false);
        byte[] data;
        var pictures = new SortedSet<int>();
        var images = 0;
        if (parameters.Mode == GrayscaleMode.Convert)
        {
            (data, var failed, images) = await Task.Run(() => ConvertColors(original, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            pictures.UnionWith(failed);
            var colored = await ColoredPagesAsync(data, [.. Enumerable.Range(0, pageCount).Except(failed)], cancellationToken).ConfigureAwait(false);
            pictures.UnionWith(colored);
        }
        else
        {
            data = original;
            pictures.UnionWith(Enumerable.Range(0, pageCount));
        }

        if (pictures.Count > 0)
        {
            data = await AsPicturesAsync(original, data, [.. pictures], withAnnotations: false, parameters.RasterDpi, cancellationToken).ConfigureAwait(false);
        }

        // A page made a picture still showing colour: an annotation did. It goes into the picture.
        var stillColored = pictures.Count > 0 ? await ColoredPagesAsync(data, [.. pictures], cancellationToken).ConfigureAwait(false) : [];
        if (stillColored.Count > 0)
        {
            data = await AsPicturesAsync(original, data, stillColored, withAnnotations: true, parameters.RasterDpi, cancellationToken).ConfigureAwait(false);
            stillColored = await ColoredPagesAsync(data, stillColored, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new OperationProgress(pageCount, pageCount, Stage));
        return (data, new GrayscaleReport(original.Length, data.Length, pageCount, images, [.. pictures], stillColored));
    }

    protected override OperationValidationResult ValidateCore(GrayscaleParams parameters, DocumentInfo document) => OperationValidationResult.Success;

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, GrayscaleParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var outputs = OptimizedFiles.Outputs(context, parameters.Overwrite);
        for (var i = 0; i < outputs.Count; i++)
        {
            progress.Report(new OperationProgress(i, outputs.Count, Stage));
            await WriteAsync(context.InputPaths[i], outputs[i], parameters, progress: null, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(new OperationProgress(outputs.Count, outputs.Count, Stage));
        return OperationResult.Success(outputs);
    }

    private static int PageCount(byte[] file)
    {
        using var document = Open(file, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    private static (byte[] Data, List<int> Failed, int Images) ConvertColors(byte[] original, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var document = Open(original, PdfDocumentOpenMode.Modify);
        var converter = GrayscaleConverter.Convert(document, progress, Stage, cancellationToken);
        using var stream = new MemoryStream();
        PdfWriting.Save(document, stream);
        return (stream.ToArray(), converter.Failed, converter.ImagesConverted);
    }

    /// <summary>Of <paramref name="pages"/>, those PDFium renders with a coloured pixel (annotations drawn).</summary>
    private Task<List<int>> ColoredPagesAsync(byte[] file, IReadOnlyList<int> pages, CancellationToken cancellationToken)
        => gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(gate, file);
                var colored = new List<int>();
                foreach (var index in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var bitmap = document.RenderPage(index, new PageRenderOptions { Scale = CheckScale, Flags = PdfRenderFlags.Annotations });
                    if (HasColor(bitmap))
                    {
                        colored.Add(index);
                    }
                }

                return colored;
            },
            PdfiumPriority.Normal,
            cancellationToken);

    private static bool HasColor(PageBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = pixels.Slice(y * bitmap.Stride, bitmap.Width * 4);
            for (var x = 0; x < row.Length; x += 4)
            {
                var (b, g, r) = (row[x], row[x + 1], row[x + 2]);
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > ColorTolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="current"/> with <paramref name="pages"/> replaced by grey pictures of themselves rendered from
    /// <paramref name="original"/>: without annotations (they stay), or with them (then they are removed, links aside).
    /// </summary>
    private async Task<byte[]> AsPicturesAsync(byte[] original, byte[] current, IReadOnlyList<int> pages, bool withAnnotations, int dpi, CancellationToken cancellationToken)
    {
        var pictures = await gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(gate, original);
                return pages.Select(index =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return (Index: index, Picture: Render(document, index, dpi, withAnnotations));
                }).ToList();
            },
            PdfiumPriority.Normal,
            cancellationToken).ConfigureAwait(false);

        return await Task.Run(
            () =>
            {
                using var document = Open(current, PdfDocumentOpenMode.Modify);
                foreach (var (index, picture) in pictures)
                {
                    var page = document.Pages[index];
                    PagePictures.Replace(document, page, picture, PictureName);
                    if (withAnnotations && Annotations(page) is { } annotations)
                    {
                        foreach (var item in annotations.Elements.ToList())
                        {
                            if (AsDictionary(item) is not { } annotation || Name(annotation, "/Subtype") != "/Link")
                            {
                                annotations.Elements.Remove(item);
                            }
                        }
                    }
                }

                using var stream = new MemoryStream();
                PdfWriting.Save(document, stream);
                return stream.ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A page rendered grey at <paramref name="dpi"/>: Flate, or JPEG when that is much smaller (a photograph).</summary>
    private static PagePicture Render(PdfiumDocument document, int pageIndex, int dpi, bool withAnnotations)
    {
        var size = document.GetPageGeometry(pageIndex).DisplaySize;
        var scale = dpi / 72.0;
        var pixels = size.Width * scale * size.Height * scale;
        if (pixels > MaxPixels)
        {
            scale *= Math.Sqrt(MaxPixels / pixels);
        }

        using var bitmap = document.RenderPage(pageIndex, new PageRenderOptions { Scale = scale, Flags = withAnnotations ? PdfRenderFlags.Annotations : PdfRenderFlags.None });
        var (width, height) = (bitmap.Width, bitmap.Height);
        var gray = new byte[width * height];
        var source = bitmap.Pixels;
        for (var y = 0; y < height; y++)
        {
            var row = source.Slice(y * bitmap.Stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                gray[(y * width) + x] = (byte)Math.Clamp(Math.Round(255 * ColorSpace.FromRgb(row[(x * 4) + 2] / 255.0, row[(x * 4) + 1] / 255.0, row[x * 4] / 255.0)), 0, 255);
            }
        }

        var flate = ImageRecompressor.Deflate(gray);
        var jpeg = JpegCodec.Encode(new PixelImage(gray, width, height, 1), 85);
        return jpeg.Length * 3 < flate.Length
            ? new PagePicture(width, height, true, jpeg, "/DCTDecode")
            : new PagePicture(width, height, true, flate, "/FlateDecode");
    }

    private static PdfDocument Open(byte[] data, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfSafeReader.Open(new MemoryStream(data, writable: false), mode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }

    private static void Check(GrayscaleParams parameters)
    {
        if (parameters.RasterDpi is < 72 or > 600)
        {
            throw new OperationValidationException(OperationId, [new OperationValidationError(nameof(parameters.RasterDpi), "The resolution must be between 72 and 600.")]);
        }
    }
}

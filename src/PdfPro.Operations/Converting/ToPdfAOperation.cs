using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Operations.Converting.PdfA;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Security;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Converting;

/// <summary>
/// convert.toPdfA (<see cref="OperationKind.Export"/>, "PDF/A"): a copy of a document made PDF/A-2b (the default) or
/// PDF/A-1b, the ISO standard for keeping documents readable in the long run. The document is opened with PDFsharp (a
/// protected one with its owner password, and the protection goes), changed where PDF/A asks for it
/// (<see cref="PdfAStructure"/>: scripts, forbidden actions, attachments, multimedia, comments, form fields, layers,
/// LZW, image alternates, PostScript, transfer functions, object metadata; an sRGB output intent), its fonts embedded
/// (<see cref="PdfAFonts"/>), its CMYK colours given a profile (<see cref="PdfAColor"/>), given document information
/// and XMP that agree (<see cref="PdfAMetadata"/>), and written as PDF 1.4 (A-1) or 1.7 (A-2) through a temporary file,
/// then read back. For PDF/A-1, pages that still use transparency are made pictures of themselves (PDFium, 200 DPI;
/// their comments stay). What PDF_Pro's own checks still find (<see cref="PdfAInspection"/>) is reported; a full
/// validation is veraPDF's.
/// </summary>
/// <param name="gate">PDFium, for the pages PDF/A-1 makes pictures of.</param>
/// <param name="fonts">The fonts embedded for fonts that are not (null: those of this computer and the bundled ones).</param>
public sealed class ToPdfAOperation(PdfiumGate gate, FontLibrary? fonts = null) : PdfOperation<ToPdfAParams>, IExportOperation
{
    public const string OperationId = "convert.toPdfA";

    Type IExportOperation.ResultType => typeof(PdfAReport);

    async Task<object> IExportOperation.ExportAsync(ExportCall call, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        return await ConvertAsync(call.Input, call.Password, call.Output, (ToPdfAParams)parameters, cancellationToken).ConfigureAwait(false);
    }

    public const string Stage = "Stage_ToPdfA";

    private const int RasterDpi = 200;
    private const double MaxPixels = 35_000_000;
    private const int PagesPerBatch = 8;
    private const string PictureName = "/PdfAPage";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Converts <paramref name="input"/> into the PDF/A file <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="DocumentPasswordException">The document is protected and the password is missing, wrong, or not the owner's.</exception>
    /// <exception cref="InvalidDataException">The document cannot be read, or what was written does not read back.</exception>
    public Task<PdfAReport> ConvertAsync(string input, string? password, string output, ToPdfAParams parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("The document does not exist.", input);
        }

        var library = fonts ?? FontLibrary.Instance;
        return OptimizedFiles.WriteAsync(
            output,
            async (temporary, token) =>
            {
                var report = await Task.Run(() => Write(input, password, temporary, output, parameters.Level, library, token), token).ConfigureAwait(false);
                if (parameters.Level == PdfALevel.A1B && report.Problems.FirstOrDefault(p => p.Kind == PdfAProblemKind.Transparency) is { Pages.Count: > 0 } transparency)
                {
                    report = await AsPicturesAsync(temporary, report, transparency.Pages, token).ConfigureAwait(false);
                }

                return report;
            },
            cancellationToken);
    }

    protected override OperationValidationResult ValidateCore(ToPdfAParams parameters, DocumentInfo document) => OperationValidationResult.Success;

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, ToPdfAParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var outputs = OptimizedFiles.Outputs(context, parameters.Overwrite);
        for (var i = 0; i < outputs.Count; i++)
        {
            progress.Report(new OperationProgress(i, outputs.Count, Stage));
            var input = context.InputPaths[i];
            await ConvertAsync(input, context.Passwords?.For(input), outputs[i], parameters, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(new OperationProgress(outputs.Count, outputs.Count, Stage));
        return OperationResult.Success(outputs);
    }

    private static PdfAReport Write(string input, string? password, string temporary, string output, PdfALevel level, FontLibrary library, CancellationToken cancellationToken)
    {
        var changes = new PdfAChanges();
        if (EncryptionWriter.LooksEncrypted(input))
        {
            changes.Add(PdfAFix.Decrypted);
        }

        using var document = EncryptionWriter.OpenForChange(input, password);
        cancellationToken.ThrowIfCancellationRequested();
        PdfAStructure.Apply(document, level, changes, cancellationToken);
        PdfAFonts.Embed(document, level, library, changes);
        if (PdfAInspection.Problems(document, level).Any(p => p.Kind == PdfAProblemKind.DeviceCmyk))
        {
            PdfAColor.DefineCmyk(document, changes);
        }

        PdfAMetadata.Write(document, level, DateTimeOffset.Now);
        changes.Add(PdfAFix.Metadata);
        var problems = PdfAInspection.Problems(document, level);
        var pages = document.PageCount;
        cancellationToken.ThrowIfCancellationRequested();
        Save(document, level, temporary);
        return new PdfAReport(output, level, pages, changes.ToList(), problems);
    }

    /// <summary>
    /// PDF/A-1: the pages that still use transparency, rendered from the converted file by PDFium (without their
    /// comments, which stay), replace their content; the file is written again and checked again.
    /// </summary>
    private async Task<PdfAReport> AsPicturesAsync(string file, PdfAReport report, IReadOnlyList<int> pages, CancellationToken cancellationToken)
    {
        var pictures = new List<(int Page, PagePicture Picture)>();
        foreach (var batch in pages.Chunk(PagesPerBatch))
        {
            pictures.AddRange(await gate.RunAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(gate, file);
                    return batch.Select(page =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return (page, Render(document, page - 1));
                    }).ToList();
                },
                PdfiumPriority.Normal,
                cancellationToken).ConfigureAwait(false));
        }

        return await Task.Run(
            () =>
            {
                PdfDocument document;
                try
                {
                    document = PdfSafeReader.Open(new MemoryStream(File.ReadAllBytes(file), writable: false), PdfDocumentOpenMode.Modify);
                }
                catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException)
                {
                    throw new InvalidDataException("The converted document does not read back.", ex);
                }

                using (document)
                {
                    foreach (var (page, picture) in pictures)
                    {
                        PagePictures.Replace(document, document.Pages[page - 1], picture, PictureName);
                    }

                    var changes = report.Changes.Append(new PdfAChange(PdfAFix.PagesAsPictures, pictures.Count)).OrderBy(c => c.Fix).ToList();
                    var problems = PdfAInspection.Problems(document, report.Level);
                    Save(document, report.Level, file);
                    return report with { Changes = changes, Problems = problems, PicturePages = [.. pictures.Select(p => p.Page)] };
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A page rendered at 200 DPI (less for a page that would pass the pixel limit), grey when it has no colour, in
    /// whichever of JPEG (quality 85) and Flate is smaller.
    /// </summary>
    private static PagePicture Render(PdfiumDocument document, int pageIndex)
    {
        var size = document.GetPageGeometry(pageIndex).DisplaySize;
        var scale = RasterDpi / 72.0;
        var pixels = size.Width * scale * size.Height * scale;
        if (pixels > MaxPixels)
        {
            scale *= Math.Sqrt(MaxPixels / pixels);
        }

        using var bitmap = document.RenderPage(pageIndex, new PageRenderOptions { Scale = scale, Flags = PdfRenderFlags.None });
        var (width, height) = (bitmap.Width, bitmap.Height);
        var rgb = new byte[width * height * 3];
        var source = bitmap.Pixels;
        for (var y = 0; y < height; y++)
        {
            var row = source.Slice(y * bitmap.Stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 3;
                (rgb[at], rgb[at + 1], rgb[at + 2]) = (row[(x * 4) + 2], row[(x * 4) + 1], row[x * 4]);
            }
        }

        var gray = true;
        for (var at = 0; gray && at < rgb.Length; at += 3)
        {
            gray = rgb[at] == rgb[at + 1] && rgb[at] == rgb[at + 2];
        }

        var samples = rgb;
        if (gray)
        {
            samples = new byte[width * height];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = rgb[i * 3];
            }
        }

        var channels = gray ? 1 : 3;
        var flate = ImageRecompressor.Deflate(samples);
        var jpeg = JpegCodec.Encode(new PixelImage(samples, width, height, channels), 85);
        return jpeg.Length < flate.Length
            ? new PagePicture(width, height, gray, jpeg, "/DCTDecode")
            : new PagePicture(width, height, gray, flate, "/FlateDecode");
    }

    /// <summary>Written as PDF 1.4 or 1.7, without the transparency group PDFsharp would give every page; then read back.</summary>
    private static void Save(PdfDocument document, PdfALevel level, string path)
    {
        document.Version = level == PdfALevel.A1B ? 14 : 17;
        document.Options.ColorMode = PdfColorMode.Undefined;
        var pages = document.PageCount;
        using (var file = File.Create(path))
        {
            PdfWriting.Save(document, file);
        }

        try
        {
            using var written = PdfSafeReader.Open(path, PdfDocumentOpenMode.Import);
            if (written.PageCount != pages)
            {
                throw new InvalidDataException("The converted document lost pages.");
            }
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException)
        {
            throw new InvalidDataException("The converted document does not read back.", ex);
        }
    }
}

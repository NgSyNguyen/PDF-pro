using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Runtime.InteropServices;
using PdfPro.Core.Documents;
using PdfPro.Core.Imaging;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Operations.Optimizing;
using PdfPro.Pdfium;
using SkiaSharp;

namespace PdfPro.Operations.Converting;

/// <summary>The file type pages are written as.</summary>
public enum PageImageFormat
{
    Png,
    Jpeg,
    Tiff,
}

/// <param name="Format">PNG, JPEG or TIFF.</param>
/// <param name="Dpi">Resolution of the pictures (a page above 50 megapixels is written smaller, see the result).</param>
/// <param name="Pages">Page range ("1-3, 8", "odd", "even"); null or empty means all pages.</param>
/// <param name="Transparent">PNG: what the page does not paint is transparent instead of white.</param>
/// <param name="JpegQuality">JPEG quality (IJG scale).</param>
/// <param name="TiffColor">TIFF: colour (LZW), grey (LZW) or black and white (CCITT G4).</param>
/// <param name="OneTiff">TIFF: all pages in one file (otherwise one file per page).</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2)").</param>
public sealed record ToImagesParams(
    PageImageFormat Format = PageImageFormat.Png,
    [property: Range(36, 1200)] int Dpi = 150,
    string? Pages = null,
    bool Transparent = false,
    [property: Range(10, 100)] int JpegQuality = 90,
    TiffColor TiffColor = TiffColor.Rgb,
    bool OneTiff = true,
    bool Overwrite = false);

/// <summary>What was written.</summary>
/// <param name="Files">The files, in page order.</param>
/// <param name="PageCount">Pages written.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="ReducedPages">Pages (0-based) written at a lower resolution to stay under 50 megapixels.</param>
public sealed record ToImagesResult(IReadOnlyList<string> Files, int PageCount, long Bytes, IReadOnlyList<int> ReducedPages);

/// <summary>
/// convert.toImages (<see cref="OperationKind.Export"/>, "PDF sang ảnh"): pages rendered by PDFium with their annotations
/// and form fields, written as PNG (optionally on a transparent background), JPEG or TIFF (one multi-page file or one
/// per page; colour and grey with LZW, black and white with CCITT G4). Files are written under temporary names and
/// renamed only when every page is done: a failure or a cancel leaves the output folder as it was.
/// </summary>
public sealed class ToImagesOperation(PdfiumGate gate) : PdfOperation<ToImagesParams>
{
    public const string OperationId = "convert.toImages";

    public const string Stage = "Stage_ToImages";

    /// <summary>Above this many pixels a page is rendered at a lower resolution.</summary>
    public const long MaxPixels = 50_000_000;

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>The file extension of a format (".png", ".jpg", ".tif").</summary>
    public static string Extension(PageImageFormat format) => format switch
    {
        PageImageFormat.Jpeg => ".jpg",
        PageImageFormat.Tiff => ".tif",
        _ => ".png",
    };

    /// <summary>
    /// Writes the pages of <paramref name="input"/> into <paramref name="outputFolder"/>, named after
    /// <paramref name="baseName"/>: "name-001.png"… (numbered by page of the document), or "name.tif" for one TIFF.
    /// </summary>
    /// <exception cref="PdfDocumentException">The file cannot be opened.</exception>
    /// <exception cref="OperationValidationException">The parameters or the page range are not valid.</exception>
    public async Task<ToImagesResult> WriteAsync(
        string input,
        string? password,
        string outputFolder,
        string baseName,
        ToImagesParams parameters,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(parameters);
        Check(parameters);
        Directory.CreateDirectory(outputFolder);
        var document = await gate.RunAsync(() => PdfiumDocument.Open(gate, input, password), PdfiumPriority.Normal, cancellationToken).ConfigureAwait(false);
        try
        {
            return await WriteAsync(document, outputFolder, baseName, parameters, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await gate.RunAsync(document.Dispose, PdfiumPriority.Normal, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<ToImagesResult> WriteAsync(
        PdfiumDocument document,
        string outputFolder,
        string baseName,
        ToImagesParams parameters,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pageCount = document.PageCount;
        if (!PageRanges.TryParse(string.IsNullOrWhiteSpace(parameters.Pages) ? "1-" + pageCount.ToString(CultureInfo.InvariantCulture) : parameters.Pages, pageCount, out var pages, out _) || pages.Count == 0)
        {
            throw new OperationValidationException(Id, [new OperationValidationError(nameof(parameters.Pages), $"The document has {pageCount} pages.")]);
        }

        var name = FileNames.Sanitize(baseName);
        if (name.Length == 0)
        {
            name = "page";
        }

        var oneFile = parameters is { Format: PageImageFormat.Tiff, OneTiff: true };
        var extension = Extension(parameters.Format);
        var digits = Math.Max(3, pageCount.ToString(CultureInfo.InvariantCulture).Length);
        IReadOnlyList<string> wanted = oneFile ? [name] : [.. pages.Select(p => $"{name}-{(p + 1).ToString("D" + digits, CultureInfo.InvariantCulture)}")];
        var finalNames = FileNames.MakeUnique(wanted, n => !parameters.Overwrite && File.Exists(Path.Combine(outputFolder, n + extension)));
        var finals = finalNames.Select(n => Path.Combine(outputFolder, n + extension)).ToList();
        var temporaries = finals.Select(f => Path.Combine(outputFolder, $".{Path.GetFileName(f)}.{Guid.NewGuid():N}.tmp")).ToList();
        var reduced = new List<int>();
        try
        {
            TiffWriter? tiff = null;
            FileStream? tiffStream = null;
            try
            {
                if (oneFile)
                {
                    tiffStream = new FileStream(temporaries[0], FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    tiff = new TiffWriter(tiffStream, pages.Count);
                }

                for (var i = 0; i < pages.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress(i, pages.Count, Stage));
                    var picture = await RenderAsync(document, pages[i], parameters, cancellationToken).ConfigureAwait(false);
                    if (picture.Reduced)
                    {
                        reduced.Add(pages[i]);
                    }

                    if (tiff is not null)
                    {
                        AddToTiff(tiff, picture, parameters.TiffColor);
                        continue;
                    }

                    await using var file = new FileStream(temporaries[i], FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    if (parameters.Format == PageImageFormat.Tiff)
                    {
                        AddToTiff(new TiffWriter(file, 1), picture, parameters.TiffColor);
                    }
                    else
                    {
                        await file.WriteAsync(Encode(picture, parameters), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (tiffStream is not null)
                {
                    await tiffStream.DisposeAsync().ConfigureAwait(false);
                }
            }

            for (var i = 0; i < finals.Count; i++)
            {
                File.Move(temporaries[i], finals[i], overwrite: parameters.Overwrite);
            }

            progress?.Report(new OperationProgress(pages.Count, pages.Count, Stage));
            return new ToImagesResult(finals, pages.Count, finals.Sum(f => new FileInfo(f).Length), reduced);
        }
        finally
        {
            foreach (var temporary in temporaries.Where(File.Exists))
            {
                File.Delete(temporary);
            }
        }
    }

    protected override OperationValidationResult ValidateCore(ToImagesParams parameters, DocumentInfo document)
        => string.IsNullOrWhiteSpace(parameters.Pages) || PageRanges.TryParse(parameters.Pages, document.PageCount, out _, out _)
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pages), $"The document has {document.PageCount} pages."));

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, ToImagesParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        for (var i = 0; i < context.InputPaths.Count; i++)
        {
            var input = context.InputPaths[i];
            var result = await WriteAsync(input, context.Passwords?.For(input), context.OutputDirectory, Path.GetFileNameWithoutExtension(input), parameters, progress, cancellationToken).ConfigureAwait(false);
            files.AddRange(result.Files);
        }

        return OperationResult.Success(files);
    }

    /// <summary>On the PDFium thread: a page rendered with its annotations, copied out of PDFium's bitmap.</summary>
    private Task<RenderedPicture> RenderAsync(PdfiumDocument document, int pageIndex, ToImagesParams parameters, CancellationToken cancellationToken)
        => gate.RunAsync(
            () =>
            {
                var size = document.GetPageGeometry(pageIndex).DisplaySize;
                var scale = parameters.Dpi / 72.0;
                var pixels = size.Width * scale * size.Height * scale;
                var reduced = pixels > MaxPixels;
                if (reduced)
                {
                    scale *= Math.Sqrt(MaxPixels / pixels) * 0.999; // pixel sizes are rounded up
                }

                var transparent = parameters is { Format: PageImageFormat.Png, Transparent: true };
                using var bitmap = document.RenderPage(pageIndex, new PageRenderOptions { Scale = scale, Flags = PdfRenderFlags.Annotations, Transparent = transparent });
                var bgra = new byte[bitmap.Width * bitmap.Height * 4];
                var source = bitmap.Pixels;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    source.Slice(y * bitmap.Stride, bitmap.Width * 4).CopyTo(bgra.AsSpan(y * bitmap.Width * 4));
                }

                if (!transparent)
                {
                    for (var i = 3; i < bgra.Length; i += 4)
                    {
                        bgra[i] = 255;
                    }
                }

                return new RenderedPicture(bgra, bitmap.Width, bitmap.Height, scale * 72, transparent, reduced);
            },
            PdfiumPriority.Normal,
            cancellationToken);

    private static byte[] Encode(RenderedPicture picture, ToImagesParams parameters)
    {
        if (parameters.Format == PageImageFormat.Jpeg)
        {
            var jpeg = JpegCodec.Encode(new PixelImage(Rgb(picture), picture.Width, picture.Height, 3), parameters.JpegQuality);
            return ImageResolution.WithJpegResolution(jpeg, picture.Dpi);
        }

        var info = new SKImageInfo(picture.Width, picture.Height, SKColorType.Bgra8888, picture.Transparent ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(picture.Bgra, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var encoded = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6))
                ?? throw new InvalidOperationException("Skia could not encode the page as PNG.");
            return ImageResolution.WithPngResolution(encoded.AsSpan(), picture.Dpi);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void AddToTiff(TiffWriter tiff, RenderedPicture picture, TiffColor color)
    {
        switch (color)
        {
            case TiffColor.Gray:
                tiff.AddGray(Gray(picture), picture.Width, picture.Height, picture.Dpi);
                break;
            case TiffColor.BlackWhite:
                tiff.AddBilevel(Bilevel(picture), picture.Width, picture.Height, picture.Dpi);
                break;
            default:
                tiff.AddRgb(Rgb(picture), picture.Width, picture.Height, picture.Dpi);
                break;
        }
    }

    private static byte[] Rgb(RenderedPicture picture)
    {
        var rgb = new byte[picture.Width * picture.Height * 3];
        for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
        {
            rgb[j] = picture.Bgra[i + 2];
            rgb[j + 1] = picture.Bgra[i + 1];
            rgb[j + 2] = picture.Bgra[i];
        }

        return rgb;
    }

    private static byte[] Gray(RenderedPicture picture)
    {
        var gray = new byte[picture.Width * picture.Height];
        for (var i = 0; i < gray.Length; i++)
        {
            var at = i * 4;
            gray[i] = (byte)(((picture.Bgra[at + 2] * 77) + (picture.Bgra[at + 1] * 150) + (picture.Bgra[at] * 29) + 128) >> 8);
        }

        return gray;
    }

    /// <summary>Black where the grey is darker than the middle; rows packed, 0 = black.</summary>
    private static byte[] Bilevel(RenderedPicture picture)
    {
        var gray = Gray(picture);
        var stride = (picture.Width + 7) / 8;
        var rows = new byte[stride * picture.Height];
        Array.Fill(rows, (byte)0xFF);
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                if (gray[(y * picture.Width) + x] < 128)
                {
                    rows[(y * stride) + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
                }
            }
        }

        return rows;
    }

    private static void Check(ToImagesParams parameters)
    {
        var errors = new List<OperationValidationError>();
        if (parameters.Dpi is < 36 or > 1200)
        {
            errors.Add(new OperationValidationError(nameof(parameters.Dpi), "The resolution must be between 36 and 1200."));
        }

        if (parameters.JpegQuality is < 10 or > 100)
        {
            errors.Add(new OperationValidationError(nameof(parameters.JpegQuality), "The JPEG quality must be between 10 and 100."));
        }

        if (errors.Count > 0)
        {
            throw new OperationValidationException(OperationId, errors);
        }
    }

    /// <summary>A page as BGRA bytes (alpha 255 unless transparent), at <paramref name="Dpi"/>.</summary>
    private sealed record RenderedPicture(byte[] Bgra, int Width, int Height, double Dpi, bool Transparent, bool Reduced);
}

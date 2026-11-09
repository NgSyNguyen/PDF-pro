using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Runtime.InteropServices;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Operations;
using PdfPro.Core.Splitting;
using PdfPro.Operations.Optimizing;
using SkiaSharp;

namespace PdfPro.Operations.Converting;

/// <summary>How the pages of pictures are sized.</summary>
public enum ImagePageSize
{
    /// <summary>Each page is its picture's size at the picture's resolution (or the default one).</summary>
    FitImage,

    /// <summary>A4, turned like the picture, the picture as large as fits, centred.</summary>
    A4Fit,

    /// <summary>A4 with a margin on every side, the picture as large as fits inside, centred.</summary>
    A4Margins,
}

/// <param name="PageSize">How pages are sized.</param>
/// <param name="MarginMillimetres">The margin of <see cref="ImagePageSize.A4Margins"/>.</param>
/// <param name="DefaultDpi">The resolution of pictures that do not give theirs (for <see cref="ImagePageSize.FitImage"/>).</param>
public sealed record FromImagesParams(
    ImagePageSize PageSize = ImagePageSize.FitImage,
    [property: Range(0.0, 50.0)] double MarginMillimetres = 10,
    [property: Range(36, 1200)] int DefaultDpi = 96);

/// <summary>What was written.</summary>
/// <param name="PageCount">Pages: one per picture, one per page of a multi-page TIFF.</param>
/// <param name="JpegsKept">JPEG pictures put into the PDF as they are (no quality lost).</param>
public sealed record FromImagesResult(string OutputPath, int PageCount, int JpegsKept);

public enum ImageFileProblem
{
    /// <summary>The file cannot be read or is not a picture.</summary>
    Unreadable,

    /// <summary>A picture of a kind not handled (12-bit JPEG, TIFF compression…).</summary>
    NotSupported,

    /// <summary>More than 200 megapixels.</summary>
    TooLarge,
}

/// <summary>A picture of the list could not be put into the PDF; nothing was written.</summary>
public sealed class ImageFileException(int fileIndex, ImageFileProblem problem, Exception? innerException = null)
    : Exception($"Picture {fileIndex + 1} cannot be converted ({problem}).", innerException)
{
    public int FileIndex { get; } = fileIndex;

    public ImageFileProblem Problem { get; } = problem;
}

/// <summary>
/// convert.fromImages (<see cref="OperationKind.Export"/>, "Ảnh sang PDF"): pictures in the order given, one page each
/// (a multi-page TIFF one page per page). JPEG goes in byte for byte (no quality lost), turned by its EXIF orientation
/// through the page's drawing; PNG, BMP, GIF and WebP are decoded by Skia and kept lossless (grey when they have no
/// colour, their transparency as a soft mask); TIFF pages are read by <see cref="TiffReader"/>, CCITT G4 and JPEG
/// strips kept as coded. Pages fit the picture or an A4 sheet. The PDF is written as it goes (one picture in memory
/// at a time) to a temporary file that replaces the output at the end.
/// </summary>
public sealed class FromImagesOperation : PdfOperation<FromImagesParams>
{
    public const string OperationId = "convert.fromImages";

    public const string Stage = "Stage_FromImages";

    /// <summary>Pictures of more pixels are refused.</summary>
    public const long MaxPixels = 200_000_000;

    private const double A4Width = 595.276;
    private const double A4Height = 841.89;

    /// <summary>The file extensions offered when picking pictures.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"];

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Writes the pictures, in order, as one PDF at <paramref name="output"/> (replaced safely).</summary>
    /// <exception cref="ImageFileException">A picture cannot be converted (its index says which).</exception>
    public Task<FromImagesResult> WriteAsync(IReadOnlyList<string> inputs, string output, FromImagesParams parameters, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(parameters);
        if (inputs.Count == 0)
        {
            throw new ArgumentException("There is no picture.", nameof(inputs));
        }

        return OptimizedFiles.WriteAsync(
            output,
            (temporary, token) => Task.Run(
                () =>
                {
                    using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    var writer = new ImagePdfWriter(stream);
                    var jpegs = 0;
                    for (var i = 0; i < inputs.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        progress?.Report(new OperationProgress(i, inputs.Count, Stage));
                        foreach (var picture in Load(inputs[i], i, parameters.DefaultDpi))
                        {
                            jpegs += picture.KeptJpeg ? 1 : 0;
                            var (page, box) = Layout(picture, parameters);
                            writer.AddPage(page, [.. picture.Pieces.Select(p => new PlacedImage(p.Image, Placement(picture, p, box)))]);
                        }
                    }

                    writer.Finish();
                    progress?.Report(new OperationProgress(inputs.Count, inputs.Count, Stage));
                    return new FromImagesResult(output, writer.PageCount, jpegs);
                },
                token),
            cancellationToken);
    }

    protected override OperationValidationResult ValidateCore(FromImagesParams parameters, DocumentInfo document) => OperationValidationResult.Success;

    /// <summary>All the inputs, in order, into one PDF named after the first in the output folder.</summary>
    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, FromImagesParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var name = FileNames.MakeUnique([Path.GetFileNameWithoutExtension(context.InputPaths[0])], n => File.Exists(Path.Combine(context.OutputDirectory, n + ".pdf")))[0];
        var output = Path.Combine(context.OutputDirectory, name + ".pdf");
        await WriteAsync(context.InputPaths, output, parameters, progress, cancellationToken).ConfigureAwait(false);
        return OperationResult.Success(output);
    }

    /// <summary>The pages a picture file gives (several for a multi-page TIFF).</summary>
    private static IEnumerable<Picture> Load(string path, int index, int defaultDpi)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImageFileException(index, ImageFileProblem.Unreadable, ex);
        }

        if (bytes is [0xFF, 0xD8, ..])
        {
            return [Jpeg(bytes, index, defaultDpi)];
        }

        if (TiffReader.IsTiff(bytes))
        {
            return Tiff(bytes, index, defaultDpi);
        }

        return [Decoded(bytes, index, defaultDpi)];
    }

    private static Picture Jpeg(byte[] bytes, int index, int defaultDpi)
    {
        var header = JpegHeader.Read(bytes) ?? throw new ImageFileException(index, ImageFileProblem.Unreadable);
        if (header.BitsPerSample != 8 || header.Components is not (1 or 3 or 4))
        {
            throw new ImageFileException(index, ImageFileProblem.NotSupported);
        }

        CheckSize(header.Width, header.Height, index);
        var space = header.Components switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", _ => "/DeviceCMYK" };

        // Adobe's CMYK JPEG are stored inverted (as Photoshop writes them): the PDF says so.
        var decode = header.Components == 4 && header.AdobeTransform is not null ? " /Decode [1 0 1 0 1 0 1 0]" : string.Empty;
        var image = new PdfImageObject($"/Type /XObject /Subtype /Image /Width {header.Width} /Height {header.Height} /ColorSpace {space} /BitsPerComponent 8 /Filter /DCTDecode{decode}", bytes);
        var (dpiX, dpiY) = ImageResolution.ReadJpeg(bytes) ?? (defaultDpi, defaultDpi);
        return new Picture(header.Width, header.Height, dpiX, dpiY, ExifOrientation.Read(bytes), [new Piece(image, 0, header.Height)], KeptJpeg: true);
    }

    private static IEnumerable<Picture> Tiff(byte[] bytes, int index, int defaultDpi)
    {
        IReadOnlyList<TiffImage> pages;
        try
        {
            pages = TiffReader.Read(bytes);
        }
        catch (InvalidDataException ex)
        {
            throw new ImageFileException(index, ex.Message.Contains("not supported", StringComparison.Ordinal) ? ImageFileProblem.NotSupported : ImageFileProblem.Unreadable, ex);
        }

        foreach (var page in pages)
        {
            CheckSize(page.Width, page.Height, index);
            IReadOnlyList<Piece> pieces = page.Data switch
            {
                TiffSamples samples => [new Piece(Samples(page.Width, page.Height, samples), 0, page.Height)],
                TiffFax fax => [.. fax.Strips.Select(s => new Piece(Fax(page.Width, s, fax.Invert), s.FirstRow, s.Rows))],
                TiffJpeg jpeg => [.. jpeg.Strips.Select(s => new Piece(JpegStrip(page.Width, s, jpeg), s.FirstRow, JpegHeader.Read(s.Data)?.Height ?? s.Rows))],
                _ => throw new ImageFileException(index, ImageFileProblem.NotSupported),
            };
            yield return new Picture(page.Width, page.Height, page.DpiX ?? defaultDpi, page.DpiY ?? page.DpiX ?? defaultDpi, page.Orientation, pieces, KeptJpeg: false);
        }
    }

    private static PdfImageObject Samples(int width, int height, TiffSamples samples)
    {
        var (space, channels) = samples.Model switch
        {
            TiffColorModel.Gray => ("/DeviceGray", 1),
            TiffColorModel.Rgb => ("/DeviceRGB", 3),
            TiffColorModel.Cmyk => ("/DeviceCMYK", 4),
            _ => ($"[/Indexed /DeviceRGB {(samples.Palette!.Length / 3) - 1} <{Convert.ToHexString(samples.Palette)}>]", 1),
        };
        var decode = samples.InvertGray && samples.Model == TiffColorModel.Gray ? " /Decode [1 0]" : string.Empty;
        var mask = samples.Alpha is { } alpha ? Lossless(alpha, width, height, 1, "/DeviceGray") : null;
        if (samples.Bits == 8 && samples.Model != TiffColorModel.Palette)
        {
            return Lossless(samples.Data, width, height, channels, space, decode) with { SoftMask = mask };
        }

        return new PdfImageObject(
            $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {space} /BitsPerComponent {samples.Bits} /Filter /FlateDecode{decode}",
            ImageRecompressor.Deflate(samples.Data),
            mask);
    }

    private static PdfImageObject Fax(int width, TiffStrip strip, bool invert)
        => new(
            $"/Type /XObject /Subtype /Image /Width {width} /Height {strip.Rows} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode " +
            $"/DecodeParms << /K -1 /Columns {width} /Rows {strip.Rows} >>{(invert ? " /Decode [1 0]" : string.Empty)}",
            strip.Data);

    private static PdfImageObject JpegStrip(int width, TiffStrip strip, TiffJpeg jpeg)
    {
        var height = JpegHeader.Read(strip.Data)?.Height ?? strip.Rows;
        var transform = jpeg.Rgb && jpeg.Components == 3 ? " /DecodeParms << /ColorTransform 0 >>" : string.Empty;
        return new PdfImageObject(
            $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {(jpeg.Components == 1 ? "/DeviceGray" : "/DeviceRGB")} /BitsPerComponent 8 /Filter /DCTDecode{transform}",
            strip.Data);
    }

    /// <summary>PNG, BMP, GIF (first frame), WebP: Skia's pixels, kept lossless; grey when no pixel has colour.</summary>
    private static Picture Decoded(byte[] bytes, int index, int defaultDpi)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new ImageFileException(index, ImageFileProblem.Unreadable);
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        CheckSize(width, height, index);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize64];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            if (codec.GetPixels(info, handle.AddrOfPinnedObject()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new ImageFileException(index, ImageFileProblem.Unreadable);
            }
        }
        finally
        {
            handle.Free();
        }

        var count = width * height;
        var (gray, opaque) = (true, true);
        for (var i = 0; i < count && (gray || opaque); i++)
        {
            var at = i * 4;
            gray &= pixels[at] == pixels[at + 1] && pixels[at + 1] == pixels[at + 2];
            opaque &= pixels[at + 3] == 255;
        }

        var channels = gray ? 1 : 3;
        var samples = new byte[count * channels];
        var alpha = opaque ? null : new byte[count];
        for (var i = 0; i < count; i++)
        {
            var at = i * 4;
            if (gray)
            {
                samples[i] = pixels[at];
            }
            else
            {
                samples[i * 3] = pixels[at];
                samples[(i * 3) + 1] = pixels[at + 1];
                samples[(i * 3) + 2] = pixels[at + 2];
            }

            if (alpha is not null)
            {
                alpha[i] = pixels[at + 3];
            }
        }

        var image = Lossless(samples, width, height, channels, gray ? "/DeviceGray" : "/DeviceRGB") with
        {
            SoftMask = alpha is null ? null : Lossless(alpha, width, height, 1, "/DeviceGray"),
        };
        var (dpiX, dpiY) = ImageResolution.ReadPng(bytes) ?? (defaultDpi, defaultDpi);
        var orientation = (int)codec.EncodedOrigin is var origin and >= 1 and <= 8 ? origin : 1;
        return new Picture(width, height, dpiX, dpiY, orientation, [new Piece(image, 0, height)], KeptJpeg: false);
    }

    /// <summary>8-bit samples, Flate with PNG predictors.</summary>
    private static PdfImageObject Lossless(byte[] samples, int width, int height, int channels, string space, string decode = "")
        => new(
            $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {space} /BitsPerComponent 8 /Filter /FlateDecode " +
            $"/DecodeParms << /Predictor 15 /Colors {channels} /BitsPerComponent 8 /Columns {width} >>{decode}",
            ImageRecompressor.Deflate(PngPredictor.Encode(samples, width, height, channels)));

    private static void CheckSize(int width, int height, int index)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ImageFileException(index, ImageFileProblem.Unreadable);
        }

        if ((long)width * height > MaxPixels)
        {
            throw new ImageFileException(index, ImageFileProblem.TooLarge);
        }
    }

    /// <summary>The page size, and the box (in PDF space) the upright picture fills.</summary>
    private static (PdfSize Page, PdfRect Box) Layout(Picture picture, FromImagesParams parameters)
    {
        var (shownWidth, shownHeight) = ExifOrientation.SwapsSides(picture.Orientation)
            ? (picture.Height / picture.DpiY * 72, picture.Width / picture.DpiX * 72)
            : (picture.Width / picture.DpiX * 72, picture.Height / picture.DpiY * 72);
        if (parameters.PageSize == ImagePageSize.FitImage)
        {
            return (new PdfSize(shownWidth, shownHeight), new PdfRect(0, 0, shownWidth, shownHeight));
        }

        var landscape = shownWidth > shownHeight;
        var page = landscape ? new PdfSize(A4Height, A4Width) : new PdfSize(A4Width, A4Height);
        var margin = parameters.PageSize == ImagePageSize.A4Margins ? parameters.MarginMillimetres * 72 / 25.4 : 0;
        var scale = Math.Min((page.Width - (2 * margin)) / shownWidth, (page.Height - (2 * margin)) / shownHeight);
        var (width, height) = (shownWidth * scale, shownHeight * scale);
        var (left, bottom) = ((page.Width - width) / 2, (page.Height - height) / 2);
        return (page, new PdfRect(left, bottom, left + width, bottom + height));
    }

    /// <summary>
    /// The image's unit square → its rows in the stored picture (pixels, y down) → the upright picture (EXIF
    /// orientation) → the box on the page (y up).
    /// </summary>
    private static PdfMatrix Placement(Picture picture, Piece piece, PdfRect box)
    {
        var (shownWidth, shownHeight) = ExifOrientation.DisplayedSize(picture.Orientation, picture.Width, picture.Height);
        return new PdfMatrix(picture.Width, 0, 0, -piece.Rows, 0, piece.FirstRow + piece.Rows)
            .Then(ExifOrientation.StoredToDisplayed(picture.Orientation, picture.Width, picture.Height))
            .Then(new PdfMatrix(box.Width / shownWidth, 0, 0, -box.Height / shownHeight, box.Left, box.Top));
    }

    /// <summary>A page to be: the stored picture's pixel size and resolution, its orientation, and its images.</summary>
    private sealed record Picture(int Width, int Height, double DpiX, double DpiY, int Orientation, IReadOnlyList<Piece> Pieces, bool KeptJpeg);

    /// <summary>An image covering rows <paramref name="FirstRow"/>… of the stored picture.</summary>
    private sealed record Piece(PdfImageObject Image, int FirstRow, int Rows);
}

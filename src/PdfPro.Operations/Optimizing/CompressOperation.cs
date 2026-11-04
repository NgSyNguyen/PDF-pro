using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>The compression presets of "Nén" (PROMPTS P9 A1).</summary>
public enum CompressionPreset
{
    /// <summary>"Màn hình": 96 DPI, JPEG quality 60.</summary>
    Screen,

    /// <summary>"eBook": 150 DPI, JPEG quality 75.</summary>
    Ebook,

    /// <summary>"In ấn": 300 DPI, JPEG quality 85.</summary>
    Print,

    /// <summary>"Lưu trữ": nothing lossy — structure, duplicates, lossless recoding only.</summary>
    Archive,

    /// <summary>"Tùy chỉnh": <see cref="CompressParams.Dpi"/> and <see cref="CompressParams.JpegQuality"/>.</summary>
    Custom,
}

/// <param name="Preset">The preset; <see cref="CompressionPreset.Custom"/> takes the two values below.</param>
/// <param name="Dpi">Custom: the resolution images are brought down to (those above 1.5 × it).</param>
/// <param name="JpegQuality">Custom: the JPEG quality of recompressed photographs (IJG scale).</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2).pdf").</param>
public sealed record CompressParams(
    CompressionPreset Preset = CompressionPreset.Ebook,
    [property: Range(36, 1200)] int Dpi = 150,
    [property: Range(10, 100)] int JpegQuality = 75,
    bool Overwrite = false)
{
    /// <summary>The image settings the preset stands for.</summary>
    internal ImageSettings Images => Preset switch
    {
        CompressionPreset.Screen => new ImageSettings(96, 60),
        CompressionPreset.Ebook => new ImageSettings(150, 75),
        CompressionPreset.Print => new ImageSettings(300, 85),
        CompressionPreset.Archive => new ImageSettings(null, 100),
        _ => new ImageSettings(Dpi, JpegQuality),
    };
}

/// <summary>What a compression did.</summary>
/// <param name="OriginalSize">Bytes of the file before.</param>
/// <param name="CompressedSize">Bytes of the file written (the original's when <paramref name="KeptOriginal"/>).</param>
/// <param name="ImagesResampled">Images brought down to the target resolution.</param>
/// <param name="ImagesRecompressed">Images whose data was written anew (resampled ones included).</param>
/// <param name="SharedCopies">Duplicate streams (pictures, fonts, profiles) replaced by one shared copy.</param>
/// <param name="StreamsRecompressed">Other streams (page content, fonts…) compressed better.</param>
/// <param name="StructureOptimized">qpdf packed the objects into object streams.</param>
/// <param name="KeptOriginal">The result was not smaller: the original was written unchanged.</param>
public sealed record CompressReport(
    long OriginalSize,
    long CompressedSize,
    int ImagesResampled,
    int ImagesRecompressed,
    int SharedCopies,
    int StreamsRecompressed,
    bool StructureOptimized,
    bool KeptOriginal)
{
    /// <summary>The share of the original saved, 0–1 (0 when nothing was).</summary>
    public double Saving => OriginalSize > 0 ? Math.Max(0, 1 - ((double)CompressedSize / OriginalSize)) : 0;
}

/// <summary>One page before and after compression, each as a one-page PDF, for a look before compressing everything.</summary>
public sealed record CompressPreview(byte[] Before, byte[] After, CompressReport Report);

/// <summary>
/// optimize.compress (<see cref="OperationKind.Export"/>, "Nén"): a smaller copy of a file (SPEC Phase 9). Duplicate
/// streams are shared; images are brought down to the preset's resolution where drawn finer than 1.5 × it, and coded
/// anew (<see cref="ImageRecompressor"/>); other streams are deflated at the highest level; qpdf, when installed, packs
/// the objects into object streams. A result that is not smaller is not written: the original is.
/// </summary>
public sealed class CompressOperation(Qpdf? qpdf = null) : PdfOperation<CompressParams>
{
    public const string OperationId = "optimize.compress";

    public const string Stage = "Stage_Compress";

    /// <summary>Streams the general recompression leaves alone.</summary>
    private static readonly HashSet<string> KeptTypes = new(StringComparer.Ordinal) { "/XRef", "/ObjStm", "/Metadata" };

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Writes a compressed copy of <paramref name="input"/> to <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="InvalidDataException">The file cannot be read (damaged, or protected by a password).</exception>
    public Task<CompressReport> WriteAsync(string input, string output, CompressParams parameters, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Check(parameters);
        return OptimizedFiles.WriteAsync(
            output,
            async (temporary, token) =>
            {
                var original = await File.ReadAllBytesAsync(input, token).ConfigureAwait(false);
                var folder = Path.GetDirectoryName(temporary)!;
                var (compressed, report) = await CompressAsync(original, parameters, folder, progress, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(temporary, compressed, token).ConfigureAwait(false);
                return report;
            },
            cancellationToken);
    }

    /// <summary>
    /// Page <paramref name="pageIndex"/> (0-based) of <paramref name="input"/> alone, before and after compression with
    /// <paramref name="parameters"/> (the structure step aside): what the images will look like.
    /// </summary>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    public async Task<CompressPreview> PreviewAsync(string input, int pageIndex, CompressParams parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Check(parameters);
        var file = await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false);
        var before = await Task.Run(
            () =>
            {
                using var source = Open(file, PdfDocumentOpenMode.Import);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, source.PageCount);
                ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
                using var single = new PdfDocument();
                single.AddPage(source.Pages[pageIndex]);
                using var stream = new MemoryStream();
                PdfWriting.Save(single, stream);
                return stream.ToArray();
            },
            cancellationToken).ConfigureAwait(false);
        var (after, report) = await CompressAsync(before, parameters, structureFolder: null, progress: null, cancellationToken).ConfigureAwait(false);
        return new CompressPreview(before, after, report);
    }

    protected override OperationValidationResult ValidateCore(CompressParams parameters, DocumentInfo document) => OperationValidationResult.Success;

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, CompressParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
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

    /// <summary>
    /// The compressed file, or the original when compressing did not make it smaller. qpdf packs the objects when
    /// <paramref name="structureFolder"/> is given: its files are written there (the output's folder, not a shared
    /// temporary one: documents may be confidential) and removed.
    /// </summary>
    internal async Task<(byte[] Data, CompressReport Report)> CompressAsync(byte[] original, CompressParams parameters, string? structureFolder, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var (data, counts) = await Task.Run(() => Compress(original, parameters.Images, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        var optimized = false;
        if (structureFolder is not null && qpdf is not null && await PackAsync(qpdf, data, structureFolder, cancellationToken).ConfigureAwait(false) is { } packed)
        {
            optimized = packed.Length < data.Length;
            data = optimized ? packed : data;
        }

        var kept = data.Length >= original.Length;
        var report = new CompressReport(original.Length, kept ? original.Length : data.Length, counts.Resampled, counts.Recompressed, counts.Shared, counts.Streams, optimized && !kept, kept);
        return (kept ? original : data, report);
    }

    private static (byte[] Data, (int Resampled, int Recompressed, int Shared, int Streams) Counts) Compress(byte[] original, ImageSettings settings, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var document = Open(original, PdfDocumentOpenMode.Modify);
        var pageCount = document.PageCount;
        var shared = StreamSharing.Share(document, cancellationToken);
        var usage = ImageUsage.Measure(document, cancellationToken);
        var candidates = ImageStreams.Candidates(usage.Sizes);

        // The images are coded in parallel. Each is decoded just before (under a lock: PDFsharp objects are not
        // thread-safe), so that only as many decoded images as threads are in memory at once.
        var outcomes = new (ImageOutcome? Outcome, int Channels)[candidates.Count];
        var reading = new Lock();
        var done = 0;
        progress?.Report(new OperationProgress(0, candidates.Count, Stage));
        Parallel.For(
            0,
            candidates.Count,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            i =>
            {
                ImageJob? job;
                lock (reading)
                {
                    job = ImageStreams.Job(candidates[i]);
                }

                outcomes[i] = job is null ? (null, 0) : (ImageRecompressor.Process(job, settings), job.Channels);
                progress?.Report(new OperationProgress(Interlocked.Increment(ref done), candidates.Count, Stage));
            });

        var handled = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var resampled = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (outcomes[i] is ({ } outcome, var channels))
            {
                ImageStreams.Write(document, candidates[i].Image, outcome, channels);
                handled.Add(candidates[i].Image);
                resampled += outcome.Resampled ? 1 : 0;
            }
        }

        var streams = RecompressStreams(document, handled, cancellationToken);
        using var output = new MemoryStream(original.Length);
        PdfWriting.Save(document, output);
        var data = output.ToArray();

        // What is written must read back, with every page.
        using (var check = Open(data, PdfDocumentOpenMode.Import))
        {
            if (check.PageCount != pageCount)
            {
                throw new InvalidDataException("The compressed document lost pages.");
            }
        }

        return (data, (resampled, handled.Count, shared, streams));
    }

    /// <summary>Streams of general-purpose filters (or none) deflated at the highest level when that makes them smaller.</summary>
    private static int RecompressStreams(PdfDocument document, HashSet<PdfDictionary> handled, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var item in document.Internals.GetAllObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not PdfDictionary { Stream.Value: { Length: > 0 } raw } stream || handled.Contains(stream)
                || Name(stream, "/Type") is { } type && KeptTypes.Contains(type) || stream.Elements.ContainsKey("/F")
                || StreamData.Decode(stream) is not { } data)
            {
                continue;
            }

            var deflated = ImageRecompressor.Deflate(data);
            if (deflated.Length < raw.Length)
            {
                stream.Stream!.Value = deflated;
                stream.Elements.SetInteger("/Length", deflated.Length);
                stream.Elements.SetName("/Filter", "/FlateDecode");
                stream.Elements.Remove("/DecodeParms");
                count++;
            }
        }

        return count;
    }

    /// <summary>The file with its objects packed by qpdf, or null when qpdf is not installed or fails on it.</summary>
    private static async Task<byte[]?> PackAsync(Qpdf qpdf, byte[] data, string folder, CancellationToken cancellationToken)
    {
        var stem = Path.Combine(folder, $".pdfpro-{Guid.NewGuid():N}");
        var (input, output) = (stem + ".in.tmp", stem + ".out.tmp");
        try
        {
            await File.WriteAllBytesAsync(input, data, cancellationToken).ConfigureAwait(false);
            await qpdf.CompactAsync(input, output, cancellationToken).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExternalToolNotFoundException or QpdfException or ExternalToolException or IOException)
        {
            return null;
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static PdfDocument Open(byte[] data, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(new MemoryStream(data, writable: false), mode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }

    private static void Check(CompressParams parameters)
    {
        var problems = new List<ValidationResult>();
        if (!Validator.TryValidateObject(parameters, new ValidationContext(parameters), problems, validateAllProperties: true))
        {
            throw new ArgumentException(problems[0].ErrorMessage, nameof(parameters));
        }
    }
}

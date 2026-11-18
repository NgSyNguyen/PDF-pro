using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Extracting;

/// <param name="Pages">Page range ("1-3, 8"); null or empty means all pages.</param>
/// <param name="MinSide">Images smaller than this many pixels on a side (icons, rules, dots) are left out; 0 keeps all.</param>
/// <param name="SkipDuplicates">An image already written (byte for byte: a logo on every page) is not written again.</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2)").</param>
public sealed record ExtractImagesParams(
    string? Pages = null,
    [property: Range(0, 10_000)] int MinSide = 0,
    bool SkipDuplicates = true,
    bool Overwrite = false);

/// <summary>What was written, and what was left out.</summary>
/// <param name="Files">The pictures, in page order.</param>
/// <param name="Duplicates">Images not written again.</param>
/// <param name="TooSmall">Images smaller than asked.</param>
/// <param name="Unreadable">Images PDFium could not decode.</param>
public sealed record ExtractImagesResult(IReadOnlyList<string> Files, int Duplicates, int TooSmall, int Unreadable);

/// <summary>
/// extract.images (<see cref="OperationKind.Export"/>, "Trích ảnh"): every image the pages draw — those inside forms
/// included — written as stored: JPEG and JPEG 2000 byte for byte (.jpg, .jp2), anything else as PNG of its own pixels
/// at its own resolution (masks are not applied). Files are named "name-003-1.jpg" (page, then order on the page),
/// written under temporary names and renamed when all are done: a failure or a cancel leaves the folder as it was.
/// </summary>
public sealed class ExtractImagesOperation(PdfiumGate gate) : PdfOperation<ExtractImagesParams>, IExportOperation
{
    public const string OperationId = "extract.images";

    Type IExportOperation.ResultType => typeof(ExtractImagesResult);

    async Task<object> IExportOperation.ExportAsync(ExportCall call, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        return await WriteAsync(call.Input, call.Password, call.Output, call.BaseName, (ExtractImagesParams)parameters, progress, cancellationToken).ConfigureAwait(false);
    }

    public const string Stage = "Stage_ExtractImages";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <exception cref="PdfDocumentException">The file cannot be opened (missing, damaged, password missing or wrong).</exception>
    /// <exception cref="OperationValidationException">The parameters or the page range are not valid.</exception>
    public async Task<ExtractImagesResult> WriteAsync(
        string input,
        string? password,
        string outputFolder,
        string baseName,
        ExtractImagesParams parameters,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(parameters);
        var document = await gate.RunAsync(() => PdfiumDocument.Open(gate, input, password), PdfiumPriority.Normal, cancellationToken).ConfigureAwait(false);
        var temporaries = new List<(string Temporary, string Wanted)>();
        try
        {
            if (!PageRanges.TryParse(string.IsNullOrWhiteSpace(parameters.Pages) ? "1-" + document.PageCount.ToString(CultureInfo.InvariantCulture) : parameters.Pages, document.PageCount, out var pages, out _) || pages.Count == 0)
            {
                throw new OperationValidationException(Id, [new OperationValidationError(nameof(parameters.Pages), $"The document has {document.PageCount} pages.")]);
            }

            var validation = Validate(parameters, new DocumentInfo(document.PageCount));
            if (!validation.IsValid)
            {
                throw new OperationValidationException(Id, validation.Errors);
            }

            var name = FileNames.Sanitize(baseName);
            if (name.Length == 0)
            {
                name = "image";
            }

            var digits = Math.Max(3, document.PageCount.ToString(CultureInfo.InvariantCulture).Length);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var (duplicates, tooSmall, unreadable) = (0, 0, 0);
            Directory.CreateDirectory(outputFolder);
            for (var i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new OperationProgress(i, pages.Count, Stage));
                var page = pages[i];
                var scan = await gate.RunAsync(() => document.ExtractPageImages(page, parameters.MinSide, cancellationToken), PdfiumPriority.Normal, cancellationToken).ConfigureAwait(false);
                (tooSmall, unreadable) = (tooSmall + scan.TooSmall, unreadable + scan.Unreadable);
                var number = 0;
                foreach (var image in scan.Images)
                {
                    if (parameters.SkipDuplicates && !seen.Add(Convert.ToHexString(SHA256.HashData(image.Data))))
                    {
                        duplicates++;
                        continue;
                    }

                    number++;
                    var wanted = $"{name}-{(page + 1).ToString("D" + digits, CultureInfo.InvariantCulture)}-{number.ToString(CultureInfo.InvariantCulture)}{image.Extension}";
                    var temporary = Path.Combine(outputFolder, $".{Guid.NewGuid():N}.tmp");
                    await File.WriteAllBytesAsync(temporary, image.Data, cancellationToken).ConfigureAwait(false);
                    temporaries.Add((temporary, wanted));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var finals = new List<string>(temporaries.Count);
            foreach (var (temporary, wanted) in temporaries)
            {
                var path = Unique(outputFolder, wanted, parameters.Overwrite);
                File.Move(temporary, path, overwrite: parameters.Overwrite);
                finals.Add(path);
            }

            progress?.Report(new OperationProgress(pages.Count, pages.Count, Stage));
            return new ExtractImagesResult(finals, duplicates, tooSmall, unreadable);
        }
        finally
        {
            foreach (var (temporary, _) in temporaries.Where(t => File.Exists(t.Temporary)))
            {
                File.Delete(temporary);
            }

            await gate.RunAsync(document.Dispose, PdfiumPriority.High, CancellationToken.None).ConfigureAwait(false);
        }
    }

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, ExtractImagesParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (var input in context.InputPaths)
        {
            var result = await WriteAsync(input, context.Passwords?.For(input), context.OutputDirectory, Path.GetFileNameWithoutExtension(input), parameters, progress, cancellationToken).ConfigureAwait(false);
            files.AddRange(result.Files);
        }

        return OperationResult.Success(files);
    }

    /// <summary>The path for a file: as wanted, or "name (2).ext" when a file is there and must not be replaced.</summary>
    private static string Unique(string folder, string fileName, bool overwrite)
    {
        var path = Path.Combine(folder, fileName);
        if (overwrite || !File.Exists(path))
        {
            return path;
        }

        var (stem, extension) = (Path.GetFileNameWithoutExtension(fileName), Path.GetExtension(fileName));
        for (var n = 2; ; n++)
        {
            path = Path.Combine(folder, $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){extension}");
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }
}

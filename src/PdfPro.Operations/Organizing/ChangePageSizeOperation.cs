using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Organizing;

/// <summary>How the content of a resized page is placed.</summary>
public enum PageSizeFit
{
    /// <summary>Scaled (keeping its proportions) to fill the new page, centred.</summary>
    ScaleToFit,

    /// <summary>Kept at its size, centred: a smaller page cuts the edges, a larger one adds margins.</summary>
    Center,
}

/// <param name="Pages">Pages to resize.</param>
/// <param name="Width">New width in points, as displayed.</param>
/// <param name="Height">New height in points, as displayed.</param>
/// <param name="Fit">How the content is placed on the new page.</param>
/// <param name="MatchOrientation">Landscape pages get the size turned to landscape (and portrait pages to portrait).</param>
public sealed record ChangePageSizeParams(
    [property: Required] string Pages,
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double Width,
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double Height,
    PageSizeFit Fit = PageSizeFit.ScaleToFit,
    bool MatchOrientation = true);

/// <summary>
/// "Đổi khổ giấy" (<see cref="OperationKind.Content"/>): gives pages a new size, scaling or centring their content
/// through a transform (text stays selectable; annotations, links and bookmarks follow). In a session it writes the
/// next revision; run on files, the changed document goes to the output folder.
/// </summary>
public sealed class ChangePageSizeOperation : PdfOperation<ChangePageSizeParams>, IContentOperation
{
    public const string OperationId = "organize.pageSize";

    /// <summary>Resource key of the progress stage.</summary>
    public const string ResizeStage = "Stage_ResizePages";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Content;

    /// <summary>The size a page gets: the requested one, turned to the page's orientation when asked.</summary>
    /// <param name="current">The page's size as displayed now.</param>
    /// <param name="parameters">The request.</param>
    public static PdfSize TargetSize(PdfSize current, ChangePageSizeParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var size = new PdfSize(parameters.Width, parameters.Height);
        return parameters.MatchOrientation && current.IsLandscape != size.IsLandscape && current.Width != current.Height
            ? new PdfSize(size.Height, size.Width)
            : size;
    }

    public async Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        if (parameters is not ChangePageSizeParams request)
        {
            throw new ArgumentException($"Expected {nameof(ChangePageSizeParams)}.", nameof(parameters));
        }

        await Task.Run(() => Resize(inputPath, output, request, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    protected override OperationValidationResult ValidateCore(ChangePageSizeParams parameters, DocumentInfo document)
    {
        var pages = ParameterChecks.Pages(parameters.Pages, document.PageCount, nameof(parameters.Pages));
        return Enum.IsDefined(parameters.Fit)
            ? pages
            : ParameterChecks.Combine(pages, OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Fit), "Unknown fit.")));
    }

    protected override async Task<OperationResult> ExecuteCoreAsync(
        OperationContext context,
        ChangePageSizeParams parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (context.InputPaths.Count != 1)
        {
            throw new ArgumentException("Changing the page size works on one file.", nameof(context));
        }

        var input = Path.GetFullPath(context.InputPaths[0]);
        var outputPath = Path.GetFullPath(Path.Combine(context.OutputDirectory, Path.GetFileName(input)));
        if (string.Equals(input, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The output would overwrite the input file; choose another output folder.");
        }

        Directory.CreateDirectory(context.OutputDirectory);
        try
        {
            var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (output.ConfigureAwait(false))
            {
                await ApplyToFileAsync(input, output, parameters, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }

        return OperationResult.Success(outputPath);
    }

    private void Resize(string inputPath, Stream output, ChangePageSizeParams request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        PdfDocument document;
        try
        {
            document = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }

        using (document)
        {
            var validation = Validate(request, new DocumentInfo(document.PageCount));
            if (!validation.IsValid)
            {
                throw new OperationValidationException(Id, validation.Errors);
            }

            var indices = PageRanges.Parse(request.Pages, document.PageCount);
            var transforms = new Dictionary<PdfObjectID, PageTransform>();
            for (var i = 0; i < indices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new OperationProgress(i, indices.Count, ResizeStage));
                var page = document.Pages[indices[i]];
                var visible = PageBoxes.VisibleBox(page);
                var turned = PageBoxes.Rotation(page) % 180 != 0;
                var current = turned ? new PdfSize(visible.Height, visible.Width) : new PdfSize(visible.Width, visible.Height);
                var reference = page.Reference ?? throw new InvalidDataException("A page is not an indirect object.");
                transforms[reference.ObjectID] = PageResizer.Resize(page, TargetSize(current, request), request.Fit == PageSizeFit.ScaleToFit);
            }

            MovedDestinations.Apply(document, transforms);
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(indices.Count, indices.Count, ResizeStage));
            Materializing.PdfWriting.Save(document, output);
        }
    }
}

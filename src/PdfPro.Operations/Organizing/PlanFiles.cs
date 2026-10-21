using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Organizing;

/// <summary>The input files of a page operation run on files: source ids, paths and page sizes.</summary>
internal sealed record PlanSources(IReadOnlyDictionary<string, string> Paths, IReadOnlyDictionary<string, IReadOnlyList<PdfSize>> PageSizes)
{
    /// <summary>Every page of the first input, untouched: what the operation edits.</summary>
    public PagePlan IdentityPlan() => PagePlan.Identity(SourceIds.Original, PageSizes[SourceIds.Original].Count, RandomPageKeys.Instance);
}

/// <summary>
/// Page operations run on files (CLI, batch, workflows): the input files become sources "s0", "s1"…, and the plan
/// the operation produces is materialized into the output folder under the first input's name. Inputs are never
/// overwritten.
/// </summary>
internal static class PlanFiles
{
    public static async Task<PlanSources> LoadAsync(IPdfDocumentLoader loader, IReadOnlyList<string> inputPaths, CancellationToken cancellationToken)
    {
        if (inputPaths.Count == 0)
        {
            throw new ArgumentException("A page operation needs at least one input file.", nameof(inputPaths));
        }

        var sizes = new Dictionary<string, IReadOnlyList<PdfSize>>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputPaths)
        {
            var id = SourceIds.Next('s', paths.Keys);
            var document = await loader.LoadAsync(input, password: null, cancellationToken).ConfigureAwait(false);
            await using (document.ConfigureAwait(false))
            {
                sizes[id] = document.PageSizes;
            }

            paths[id] = Path.GetFullPath(input);
        }

        return new PlanSources(paths, sizes);
    }

    /// <returns>The written file.</returns>
    public static async Task<string> WriteAsync(
        IPlanMaterializer materializer,
        OperationContext context,
        PagePlan plan,
        PlanSources sources,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.GetFullPath(Path.Combine(context.OutputDirectory, Path.GetFileName(context.InputPaths[0])));
        if (sources.Paths.Values.Any(p => string.Equals(p, outputPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The output would overwrite an input file; choose another output folder.");
        }

        Directory.CreateDirectory(context.OutputDirectory);
        try
        {
            var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (output.ConfigureAwait(false))
            {
                var request = new MaterializeRequest
                {
                    Plan = plan,
                    BaseSourceId = SourceIds.Original,
                    SourcePaths = sources.Paths,
                    WorkingDirectory = context.WorkingDirectory,
                };
                await materializer.MaterializeAsync(request, output, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }

        return outputPath;
    }
}

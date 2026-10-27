using System.Text;
using System.Text.RegularExpressions;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Operations.Organizing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Stamping;

/// <summary>
/// stamp.remove: takes off the watermarks, backgrounds, headers and footers, page numbers or Bates numbers that
/// PDF_Pro added — recognised by the marker on their content stream — together with the resources they used, and drops
/// layers no page uses any more. The page then shows exactly what it showed before the stamp. Stamps made by other
/// programs, or files whose content streams another program merged, are left as they are.
/// </summary>
public sealed partial class RemoveStampsOperation : DocumentContentOperation<RemoveStampsParams>
{
    public const string OperationId = "stamp.remove";

    public override string Id => OperationId;

    public override string Stage => "Stage_RemoveStamps";

    /// <summary>How many pages carry each kind of PDF_Pro stamp (kinds absent from the file are not listed).</summary>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    public static IReadOnlyDictionary<StampKind, int> Scan(string path)
    {
        using var document = OpenForChange(path);
        var counts = new Dictionary<StampKind, int>();
        foreach (var page in document.Pages)
        {
            foreach (var kind in PageStamper.ContentStreams(page).Select(KindOf).OfType<StampKind>().Distinct())
            {
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
        }

        return counts;
    }

    protected override OperationValidationResult ValidateCore(RemoveStampsParams parameters, DocumentInfo document)
        => ParameterChecks.Combine(
            parameters.Kinds.All(Enum.IsDefined)
                ? OperationValidationResult.Success
                : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Kinds), "Unknown kind of stamp.")),
            StampContent.CheckPages(parameters.Pages, document, nameof(parameters.Pages)));

    protected override void Apply(PdfDocument document, string inputPath, RemoveStampsParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var kinds = parameters.Kinds.ToHashSet();
        var pages = StampContent.Pages(parameters.Pages, document.PageCount);
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(i, pages.Count, Stage));
            var page = document.Pages[pages[i]];
            var streams = PageStamper.ContentStreams(page);
            var removed = streams.Where(s => KindOf(s) is { } kind && kinds.Contains(kind)).ToArray();
            if (removed.Length == 0)
            {
                continue;
            }

            var contents = new PdfArray(document);
            streams.Except(removed).ToList().ForEach(contents.Elements.Add);
            page.Elements["/Contents"] = contents;
            RemoveResources(page, removed);
        }

        DropUnusedLayers(document);
        progress.Report(new OperationProgress(pages.Count, pages.Count, Stage));
    }

    private static StampKind? KindOf(PdfItem stream)
        => AsDictionary(stream) is { } dictionary && Name(dictionary, PageStamper.MarkerKey) is { Length: > 1 } marker
            && Enum.TryParse<StampKind>(marker[1..], out var kind)
            ? kind
            : null;

    /// <summary>Removes the forms, opacity states and layer properties the removed streams named (they were this page's own).</summary>
    private static void RemoveResources(PdfPage page, IEnumerable<PdfItem> removed)
    {
        if (AsDictionary(page.Elements["/Resources"]) is not { } resources)
        {
            return;
        }

        foreach (var stream in removed)
        {
            var text = Encoding.ASCII.GetString(AsDictionary(stream)?.Stream?.UnfilteredValue ?? []);
            foreach (Match match in ResourceName().Matches(text))
            {
                var key = match.Groups["kind"].Value switch { "X" => "/XObject", "OC" => "/Properties", _ => "/ExtGState" };
                AsDictionary(resources.Elements[key])?.Elements.Remove(match.Value);
            }
        }
    }

    private static void DropUnusedLayers(PdfDocument document)
    {
        var catalog = document.Internals.Catalog;
        if (AsDictionary(catalog.Elements["/OCProperties"]) is not { } properties || AsArray(properties.Elements["/OCGs"]) is not { } groups)
        {
            return;
        }

        var used = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        foreach (var page in document.Pages)
        {
            if (AsDictionary(AsDictionary(page.Elements["/Resources"])?.Elements["/Properties"]) is { } names)
            {
                foreach (var value in names.Elements.Values)
                {
                    if (Resolve(value) is PdfObject target)
                    {
                        used.Add(target);
                    }
                }
            }
        }

        var unused = groups.Elements
            .Where(g => AsDictionary(g) is { } group && group.Elements.ContainsKey(PageStamper.MarkerKey) && !used.Contains(group))
            .Select(Resolve)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        if (unused.Count == 0)
        {
            return;
        }

        RemoveFrom(groups, unused);
        if (AsDictionary(properties.Elements["/D"]) is { } configuration)
        {
            foreach (var key in new[] { "/ON", "/OFF", "/Order" })
            {
                if (AsArray(configuration.Elements[key]) is { } list)
                {
                    RemoveFrom(list, unused);
                }
            }
        }

        if (groups.Elements.Count == 0)
        {
            catalog.Elements.Remove("/OCProperties");
        }
    }

    private static void RemoveFrom(PdfArray array, HashSet<object?> targets)
    {
        for (var i = array.Elements.Count - 1; i >= 0; i--)
        {
            if (targets.Contains(Resolve(array.Elements[i])))
            {
                array.Elements.RemoveAt(i);
            }
        }
    }

    [GeneratedRegex(@"/PdfPro(?<kind>X|OC|GS)\d+")]
    private static partial Regex ResourceName();
}

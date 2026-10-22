using PdfPro.Core.Operations;
using PdfPro.Core.Splitting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Splitting;

/// <summary>What each page would add to a split file, and what every file costs anyway.</summary>
public sealed record SizeEstimate(IReadOnlyList<IReadOnlyList<SizedPart>> Parts, long OverheadBytes)
{
    public IReadOnlyList<SizedPart> PartsOf(int index) => Parts[index];
}

/// <summary>
/// Split by size, step one: the objects each page needs (content streams, images, fonts, annotations), read with
/// PDFsharp, so that a resource shared by several pages counts once per file. The estimate is checked against the
/// real size when the files are written.
/// </summary>
public static class PageSizeEstimator
{
    /// <summary>Resource key of the progress stage.</summary>
    public const string Stage = "Stage_SplitMeasureSize";

    /// <summary>Header, trailer and cross-reference table of a small file.</summary>
    private const long FileOverhead = 1_500;

    /// <summary>A new blank page (page dictionary and empty content).</summary>
    private const long BlankPageBytes = 300;

    public static Task<SizeEstimate> EstimateAsync(SplitDocument document, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(progress);
        return Task.Run(() => Estimate(document, progress, cancellationToken), cancellationToken);
    }

    private static SizeEstimate Estimate(SplitDocument document, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var plan = document.Plan;
        var sourceIds = plan.GetSourceIds().Append(document.BaseSourceId).Distinct().ToArray();
        var tags = sourceIds.Select((id, i) => (id, tag: (long)(i + 1) << 32)).ToDictionary(x => x.id, x => x.tag, StringComparer.Ordinal);
        var opened = new Dictionary<string, PdfDocument?>(StringComparer.Ordinal);
        try
        {
            foreach (var id in sourceIds)
            {
                opened[id] = TryOpen(document.SourcePaths[id]);
            }

            var overhead = FileOverhead + (opened[document.BaseSourceId] is { } baseDocument ? CatalogBytes(baseDocument) : 0);
            var cache = new Dictionary<(string, int), IReadOnlyList<SizedPart>>();
            var parts = new IReadOnlyList<SizedPart>[plan.Count];
            for (var i = 0; i < plan.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new OperationProgress(i, plan.Count, Stage));
                var page = plan[i];
                if (page.IsBlank)
                {
                    parts[i] = [new SizedPart(-1 - i, BlankPageBytes)];
                    continue;
                }

                var key = (page.SourceId, page.SourcePageIndex);
                if (!cache.TryGetValue(key, out var pageParts))
                {
                    pageParts = opened[page.SourceId] is { } source
                        ? PartsOfPage(source.Pages[page.SourcePageIndex], tags[page.SourceId])
                        : [new SizedPart(tags[page.SourceId] | (uint)page.SourcePageIndex, Fallback(document.SourcePaths[page.SourceId], document.Documents[page.SourceId].PageCount))];
                    cache[key] = pageParts;
                }

                parts[i] = pageParts;
            }

            progress.Report(new OperationProgress(plan.Count, plan.Count, Stage));
            return new SizeEstimate(parts, overhead);
        }
        finally
        {
            foreach (var source in opened.Values)
            {
                source?.Dispose();
            }
        }
    }

    /// <summary>The source read-only, or null when PDFsharp cannot parse it (the estimate then falls back to an average).</summary>
    private static PdfDocument? TryOpen(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return null;
        }
    }

    private static long Fallback(string path, int pageCount) => new FileInfo(path).Length / Math.Max(1, pageCount);

    /// <summary>Every object reachable from the page, without following other pages or parents.</summary>
    private static IReadOnlyList<SizedPart> PartsOfPage(PdfPage page, long tag)
    {
        var parts = new List<SizedPart>();
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance) { page };
        parts.Add(new SizedPart(tag | (uint)(page.Reference?.ObjectNumber ?? 0), SizeOf(page)));
        Walk(page, tag, visited, parts, skipKeys: ["/Parent", "/P"]);
        return parts;
    }

    /// <summary>What the base document's catalog carries besides pages (outline, names, forms, metadata).</summary>
    private static long CatalogBytes(PdfDocument document)
    {
        var parts = new List<SizedPart>();
        var catalog = document.Internals.Catalog;
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance) { catalog };
        Walk(catalog, 0, visited, parts, skipKeys: ["/Pages", "/Parent", "/P"]);
        return SizeOf(catalog) + parts.Sum(p => p.Bytes);
    }

    private static void Walk(PdfDictionary root, long tag, HashSet<PdfObject> visited, List<SizedPart> parts, string[] skipKeys)
    {
        var pending = new Stack<PdfItem>();
        PushChildren(root, pending, skipKeys);
        while (pending.TryPop(out var item))
        {
            switch (item)
            {
                case PdfReference reference:
                    var target = reference.Value;
                    if (target is null || IsPage(target) || (target is PdfDictionary d && Name(d, "/Type") == "/Pages") || !visited.Add(target))
                    {
                        break;
                    }

                    parts.Add(new SizedPart(tag | (uint)reference.ObjectNumber, SizeOf(target)));
                    pending.Push(target);
                    break;
                case PdfDictionary dictionary:
                    PushChildren(dictionary, pending, skipKeys);
                    break;
                case PdfArray array:
                    foreach (var element in array.Elements)
                    {
                        pending.Push(element);
                    }

                    break;
            }
        }
    }

    private static void PushChildren(PdfDictionary dictionary, Stack<PdfItem> pending, string[] skipKeys)
    {
        foreach (var (key, value) in dictionary.Elements)
        {
            if (value is not null && Array.IndexOf(skipKeys, key) < 0)
            {
                pending.Push(value);
            }
        }
    }

    /// <summary>Bytes an object takes in the file, roughly (stream data exact, dictionaries estimated).</summary>
    private static long SizeOf(PdfObject value) => value switch
    {
        PdfDictionary { Stream: { } stream } dictionary => 40 + stream.Length + (dictionary.Elements.Count * 16),
        PdfDictionary dictionary => 40 + (dictionary.Elements.Count * 16),
        PdfArray array => 30 + (array.Elements.Count * 10),
        _ => 40,
    };
}

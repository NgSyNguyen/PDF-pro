using PdfPro.Core.Geometry;
using PdfPro.Operations.Materializing;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.TestSupport;

/// <summary>A link annotation of an output file: its page and where it jumps (page index, or null when external).</summary>
internal sealed record LinkInfo(int Page, int? TargetPage, string? Uri);

/// <summary>Reads materialized files back, with PDFium (what users see) and PDFsharp (the file structure).</summary>
internal static class PdfInspector
{
    public static async Task<PdfDocumentHandle> OpenAsync(PdfiumGate gate, string path)
        => await PdfDocumentHandle.OpenAsync(gate, path, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>The text of the given pages, whitespace-collapsed.</summary>
    public static async Task<string[]> TextsAsync(PdfDocumentHandle document, params int[] pages)
    {
        var texts = new string[pages.Length];
        for (var i = 0; i < pages.Length; i++)
        {
            var raw = await document.GetPageTextAsync(pages[i], TestContext.Current.CancellationToken);
            texts[i] = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        return texts;
    }

    public static async Task<string[]> AllTextsAsync(PdfDocumentHandle document)
        => await TextsAsync(document, [.. Enumerable.Range(0, document.PageCount)]);

    public static async Task<PageGeometry[]> GeometriesAsync(PdfDocumentHandle document)
    {
        var result = new PageGeometry[document.PageCount];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = await document.GetPageGeometryAsync(i, TestContext.Current.CancellationToken);
        }

        return result;
    }

    /// <summary>Bookmarks flattened as "title→page" (page null when the bookmark has no destination), depth-first.</summary>
    public static async Task<string[]> BookmarksAsync(PdfDocumentHandle document)
    {
        var result = new List<string>();
        Flatten(await document.GetBookmarksAsync(TestContext.Current.CancellationToken), depth: 0, result);
        return [.. result];
    }

    /// <summary>Every link annotation, resolving destinations to page indices.</summary>
    public static LinkInfo[] Links(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var names = new NamedDestinations(document);
        var pageIndex = Enumerable.Range(0, document.PageCount).ToDictionary<int, PdfObject, int>(i => document.Pages[i], i => i, ReferenceEqualityComparer.Instance);
        var links = new List<LinkInfo>();
        for (var i = 0; i < document.PageCount; i++)
        {
            foreach (var annotation in AnnotationsOf(document.Pages[i]))
            {
                if (Name(annotation, "/Subtype") != "/Link")
                {
                    continue;
                }

                var uri = AsDictionary(annotation.Elements["/A"]) is { } action && Name(action, "/S") == "/URI"
                    ? action.Elements.GetString("/URI")
                    : null;
                int? target = Destinations.Of(annotation, names) is { Elements.Count: > 0 } destination
                    && Resolve(destination.Elements[0]) is PdfObject page && pageIndex.TryGetValue(page, out var index)
                    ? index
                    : null;
                links.Add(new LinkInfo(i, target, uri));
            }
        }

        return [.. links];
    }

    /// <summary>The annotations of each page as (subtype, object number, /P page index or -1).</summary>
    public static (string Subtype, int ObjectNumber, int OwnerPage)[][] Annotations(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var pageIndex = Enumerable.Range(0, document.PageCount).ToDictionary<int, PdfObject, int>(i => document.Pages[i], i => i, ReferenceEqualityComparer.Instance);
        return Enumerable.Range(0, document.PageCount)
            .Select(i => AnnotationsOf(document.Pages[i])
                .Select(a => (
                    Name(a, "/Subtype") ?? "?",
                    a.Reference?.ObjectNumber ?? -1,
                    Resolve(a.Elements["/P"]) is PdfObject owner && pageIndex.TryGetValue(owner, out var p) ? p : -1))
                .ToArray())
            .ToArray();
    }

    /// <summary>
    /// Number of page objects stored in the file, in or out of the page tree: more than the page count means pages
    /// were dragged in by references (or deleted pages stayed behind).
    /// </summary>
    public static int PageObjectCount(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.Internals.GetAllObjects().Count(IsPage);
    }

    /// <summary>Runs a check against the raw catalog of a file.</summary>
    public static T Catalog<T>(string path, Func<PdfDictionary, T> read)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return read(document.Internals.Catalog);
    }

    private static IEnumerable<PdfDictionary> AnnotationsOf(PdfPage page)
        => PdfGraph.Annotations(page)?.Elements.Select(AsDictionary).OfType<PdfDictionary>() ?? [];

    private static void Flatten(IReadOnlyList<PdfBookmark> bookmarks, int depth, List<string> result)
    {
        foreach (var bookmark in bookmarks)
        {
            result.Add($"{new string('>', depth)}{bookmark.Title}→{bookmark.Destination?.PageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
            Flatten(bookmark.Children, depth + 1, result);
        }
    }
}

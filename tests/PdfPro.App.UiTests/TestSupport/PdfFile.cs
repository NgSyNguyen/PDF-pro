using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.UiTests.TestSupport;

/// <summary>A page of a saved file as PDFsharp reads it: its /Rotate and media box size in whole points.</summary>
internal sealed record PdfPageInfo(int Rotate, int Width, int Height);

/// <summary>A bookmark of a saved file: its title, 0-based target page (-1 without one) and depth (0 = top level).</summary>
internal sealed record PdfBookmark(string Title, int Page, int Depth);

/// <summary>Reads files the app wrote with another library than the app's renderer.</summary>
internal static class PdfFile
{
    public static PdfPageInfo[] Pages(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return
        [
            .. Enumerable.Range(0, document.PageCount).Select(i =>
            {
                var page = document.Pages[i];
                return new PdfPageInfo(page.Elements.GetInteger("/Rotate"), (int)Math.Round(page.MediaBox.Width), (int)Math.Round(page.MediaBox.Height));
            }),
        ];
    }

    public static int PageCount(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    /// <summary>True when the file opens and has <paramref name="pageCount"/> pages.</summary>
    public static bool Opens(string path, int pageCount)
    {
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return document.PageCount == pageCount;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PdfReaderException)
        {
            return false;
        }
    }

    /// <summary>The outline, depth-first, with destinations given as page arrays (directly or through a GoTo action).</summary>
    public static PdfBookmark[] Bookmarks(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var pages = PageIndices(document);
        var result = new List<PdfBookmark>();
        if (Resolve(document.Internals.Catalog.Elements["/Outlines"]) is PdfDictionary outlines)
        {
            AddChildren(outlines, 0, pages, result);
        }

        return [.. result];
    }

    /// <summary>The target pages of the link annotations on page <paramref name="pageIndex"/>, in their order.</summary>
    public static int[] LinkTargets(string path, int pageIndex)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var pages = PageIndices(document);
        if (Resolve(document.Pages[pageIndex].Elements["/Annots"]) is not PdfArray annotations)
        {
            return [];
        }

        return
        [
            .. annotations.Elements
                .Select(Resolve)
                .OfType<PdfDictionary>()
                .Where(a => a.Elements.GetName("/Subtype") == "/Link")
                .Select(a => TargetPage(a, pages)),
        ];
    }

    /// <summary>The names of the file's optional content groups (layers): "Watermark", "Background", "Header/Footer"…</summary>
    public static string[] Layers(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return Resolve(document.Internals.Catalog.Elements["/OCProperties"]) is PdfDictionary properties
            && Resolve(properties.Elements["/OCGs"]) is PdfArray groups
            ? [.. groups.Elements.Select(Resolve).OfType<PdfDictionary>().Select(g => g.Elements.GetString("/Name"))]
            : [];
    }

    /// <summary>The text of every page as PdfPig reads it, NFC, without white space (so layout does not matter).</summary>
    public static string[] PageTexts(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return
        [
            .. document.GetPages().Select(page => string.Concat(page.Letters.Select(l => l.Value).Where(v => !string.IsNullOrWhiteSpace(v)))
                .Normalize(System.Text.NormalizationForm.FormC)),
        ];
    }

    private static void AddChildren(PdfDictionary parent, int depth, Dictionary<PdfObject, int> pages, List<PdfBookmark> result)
    {
        var item = Resolve(parent.Elements["/First"]) as PdfDictionary;
        while (item is not null && result.Count < 100_000)
        {
            result.Add(new PdfBookmark(item.Elements.GetString("/Title"), TargetPage(item, pages), depth));
            AddChildren(item, depth + 1, pages, result);
            item = Resolve(item.Elements["/Next"]) as PdfDictionary;
        }
    }

    private static int TargetPage(PdfDictionary item, Dictionary<PdfObject, int> pages)
    {
        var destination = Resolve(item.Elements["/Dest"]) as PdfArray
            ?? (Resolve(item.Elements["/A"]) is PdfDictionary action ? Resolve(action.Elements["/D"]) as PdfArray : null);
        return destination is { Elements.Count: > 0 } && Resolve(destination.Elements[0]) is PdfObject page && pages.TryGetValue(page, out var index)
            ? index
            : -1;
    }

    private static Dictionary<PdfObject, int> PageIndices(PdfDocument document)
        => Enumerable.Range(0, document.PageCount).ToDictionary<int, PdfObject, int>(i => document.Pages[i], i => i, ReferenceEqualityComparer.Instance);

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
}

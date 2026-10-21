using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Materializing;

/// <summary>
/// The named destinations of a document: the /Dests dictionary (PDF 1.1, keys are names) and the /Names /Dests
/// name tree (keys are strings). Keys are stored without the leading slash of names.
/// </summary>
internal sealed class NamedDestinations
{
    private const int MaxTreeDepth = 64;

    private readonly Dictionary<string, PdfArray> _destinations = new(StringComparer.Ordinal);

    public NamedDestinations(PdfDocument document)
    {
        var catalog = document.Internals.Catalog;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (AsDictionary(AsDictionary(catalog.Elements["/Names"])?.Elements["/Dests"]) is { } tree)
        {
            ReadTree(tree, 0, visited);
        }

        if (AsDictionary(catalog.Elements["/Dests"]) is { } legacy)
        {
            foreach (var key in legacy.Elements.Keys)
            {
                if (Destinations.Explicit(legacy.Elements[key], this) is { } destination)
                {
                    _destinations.TryAdd(key.TrimStart('/'), destination);
                }
            }
        }
    }

    public PdfArray? Find(string name) => _destinations.GetValueOrDefault(name.TrimStart('/'));

    /// <summary>Every named destination.</summary>
    public IEnumerable<PdfArray> All => _destinations.Values;

    private void ReadTree(PdfDictionary node, int depth, HashSet<PdfDictionary> visited)
    {
        if (depth > MaxTreeDepth || !visited.Add(node))
        {
            return;
        }

        if (AsArray(node.Elements["/Names"]) is { } pairs)
        {
            for (var i = 0; i + 1 < pairs.Elements.Count; i += 2)
            {
                if (Resolve(pairs.Elements[i]) is PdfString key && Destinations.Explicit(pairs.Elements[i + 1], this) is { } destination)
                {
                    _destinations.TryAdd(key.Value, destination);
                }
            }
        }

        if (AsArray(node.Elements["/Kids"]) is { } kids)
        {
            foreach (var kid in kids.Elements)
            {
                if (AsDictionary(kid) is { } child)
                {
                    ReadTree(child, depth + 1, visited);
                }
            }
        }
    }
}

/// <summary>Reading the internal destinations of links, bookmarks and open actions.</summary>
internal static class Destinations
{
    /// <summary>True when the link or bookmark jumps inside the document (/Dest or a GoTo action).</summary>
    public static bool IsInternal(PdfDictionary owner)
        => owner.Elements.ContainsKey("/Dest") || GoToAction(owner) is not null;

    /// <summary>The explicit destination array of a link or bookmark, resolving named destinations; null if none.</summary>
    public static PdfArray? Of(PdfDictionary owner, NamedDestinations names)
        => Explicit(owner.Elements["/Dest"] ?? GoToAction(owner)?.Elements["/D"], names);

    /// <summary>An explicit destination array from any destination form (array, name, string, dictionary with /D).</summary>
    public static PdfArray? Explicit(PdfItem? destination, NamedDestinations? names) => Resolve(destination) switch
    {
        PdfArray array => array,
        PdfName name => names?.Find(name.Value),
        PdfString text => names?.Find(text.Value),
        PdfDictionary dictionary => Explicit(dictionary.Elements["/D"], names),
        _ => null,
    };

    /// <summary>True when the destination's page was deleted (its page entry was replaced by null).</summary>
    public static bool IsDead(PdfArray? destination)
        => destination is { Elements.Count: > 0 } && destination.Elements[0] is PdfNull;

    private static PdfDictionary? GoToAction(PdfDictionary owner)
        => AsDictionary(owner.Elements["/A"]) is { } action && Name(action, "/S") == "/GoTo" ? action : null;
}

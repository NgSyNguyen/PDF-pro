using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfPro.Operations.Materializing;

/// <summary>Small helpers over the PDFsharp object model.</summary>
internal static class PdfGraph
{
    /// <summary>The object an item stands for: the target of a reference, or the item itself.</summary>
    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    public static PdfDictionary? AsDictionary(PdfItem? item) => Resolve(item) as PdfDictionary;

    public static PdfArray? AsArray(PdfItem? item) => Resolve(item) as PdfArray;

    /// <summary>A name entry such as "/Link", or null.</summary>
    public static string? Name(PdfDictionary dictionary, string key) => Resolve(dictionary.Elements[key]) is PdfName name ? name.Value : null;

    public static bool IsPage(PdfObject? value) => value is PdfDictionary dictionary && Name(dictionary, "/Type") == "/Page";

    /// <summary>The /Annots array of a page (resolving an indirect array), or null.</summary>
    public static PdfArray? Annotations(PdfDictionary page) => AsArray(page.Elements["/Annots"]);

    /// <summary>
    /// Cuts every reference to an object matching <paramref name="isTarget"/> in the graph reachable from
    /// <paramref name="root"/>: dictionary entries are removed, array elements become null. Targets are not entered.
    /// </summary>
    public static void RemoveReferences(PdfItem root, Func<PdfObject, bool> isTarget)
    {
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<PdfItem>();
        pending.Push(root);
        if (root is PdfObject rootObject)
        {
            visited.Add(rootObject);
        }

        while (pending.TryPop(out var item))
        {
            if (item is PdfDictionary dictionary)
            {
                foreach (var key in dictionary.Elements.Keys.ToArray())
                {
                    if (Visit(dictionary.Elements[key], isTarget, visited, pending))
                    {
                        dictionary.Elements.Remove(key);
                    }
                }
            }
            else if (item is PdfArray array)
            {
                for (var i = 0; i < array.Elements.Count; i++)
                {
                    if (Visit(array.Elements[i], isTarget, visited, pending))
                    {
                        array.Elements[i] = PdfNull.Value;
                    }
                }
            }
        }
    }

    /// <summary>Queues what <paramref name="value"/> leads to; true when it references a target and must be cut.</summary>
    private static bool Visit(PdfItem? value, Func<PdfObject, bool> isTarget, HashSet<PdfObject> visited, Stack<PdfItem> pending)
    {
        switch (value)
        {
            case PdfReference { Value: { } target }:
                if (isTarget(target))
                {
                    return true;
                }

                if (visited.Add(target))
                {
                    pending.Push(target);
                }

                return false;
            case PdfDictionary or PdfArray:
                // Direct objects form a tree inside their indirect parent: no cycles to guard against.
                pending.Push(value);
                return false;
            default:
                return false;
        }
    }
}

using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Annotating;

/// <summary>Conversations of annotations in a page: the answers that point at an annotation through /IRT.</summary>
internal static class AnnotationThreads
{
    /// <summary>
    /// Removes annotations from their page with their answers (whole threads, answers to answers too) and pop-up
    /// windows. The page gets an /Annots array of its own.
    /// </summary>
    public static void Remove(PdfDocument document, PdfPage page, IReadOnlyCollection<PdfDictionary> removed)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(removed);
        var annotations = Annotations(page);
        if (annotations is null || removed.Count == 0)
        {
            return;
        }

        var gone = new HashSet<PdfDictionary>(removed, ReferenceEqualityComparer.Instance);
        var all = annotations.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList();
        bool changed;
        do
        {
            // Answers to removed annotations go too, and answers to those, until no more are found.
            changed = false;
            foreach (var annotation in all.Where(a => !gone.Contains(a)))
            {
                if (AsDictionary(annotation.Elements["/IRT"]) is { } parent && gone.Contains(parent))
                {
                    gone.Add(annotation);
                    changed = true;
                }
            }
        }
        while (changed);

        foreach (var annotation in gone.ToList())
        {
            if (AsDictionary(annotation.Elements["/Popup"]) is { } popup)
            {
                gone.Add(popup);
            }
        }

        // A pop-up whose annotation is gone is left without a window to belong to.
        foreach (var annotation in all.Where(a => !gone.Contains(a) && Name(a, "/Subtype") == "/Popup"))
        {
            if (AsDictionary(annotation.Elements["/Parent"]) is { } parent && gone.Contains(parent))
            {
                gone.Add(annotation);
            }
        }

        var kept = new PdfArray(document);
        foreach (var item in annotations.Elements)
        {
            if (AsDictionary(item) is not { } annotation || !gone.Contains(annotation))
            {
                kept.Elements.Add(item);
            }
        }

        page.Elements["/Annots"] = kept;
    }

    /// <summary>An answer in another annotation's conversation (/IRT without /RT /Group: grouped ones are drawn).</summary>
    public static bool IsAnswer(PdfDictionary annotation)
        => annotation.Elements.ContainsKey("/IRT") && Name(annotation, "/RT") != "/Group";
}

using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Materializing;

/// <summary>
/// An annotation belongs to exactly one page (PDF 32000-1, 12.5.2). Copied pages (duplicates, the same foreign page
/// imported twice) still list the original annotations; this pass gives each later page its own copies.
/// </summary>
internal static class AnnotationOwnership
{
    // Entries that link annotations of the same page together (markup ↔ popup, replies).
    private static readonly string[] SiblingKeys = ["/Popup", "/Parent", "/IRT"];

    public static void EnsureUnique(PdfDocument document, IEnumerable<PdfPage> pages)
    {
        var owned = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var arrays = new HashSet<PdfArray>(ReferenceEqualityComparer.Instance);
        foreach (var page in pages)
        {
            if (Annotations(page) is not { } annotations)
            {
                continue;
            }

            if (!arrays.Add(annotations))
            {
                annotations = annotations.Clone();
                page.Elements["/Annots"] = annotations;
                arrays.Add(annotations);
            }

            var copies = new Dictionary<PdfDictionary, PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (var i = annotations.Elements.Count - 1; i >= 0; i--)
            {
                if (AsDictionary(annotations.Elements[i]) is not { } annotation || owned.Add(annotation))
                {
                    continue;
                }

                if (Name(annotation, "/Subtype") == "/Widget")
                {
                    // A form field widget cannot simply be copied: the field would appear twice. Forms come in Phase 7.
                    annotations.Elements.RemoveAt(i);
                    continue;
                }

                var copy = annotation.Clone();
                document.Internals.AddObject(copy);
                owned.Add(copy);
                copies[annotation] = copy;
                annotations.Elements[i] = copy.Reference!;
            }

            foreach (var copy in copies.Values)
            {
                foreach (var key in SiblingKeys)
                {
                    if (AsDictionary(copy.Elements[key]) is { } sibling && copies.TryGetValue(sibling, out var siblingCopy))
                    {
                        copy.Elements[key] = siblingCopy.Reference!;
                    }
                }
            }

            foreach (var item in annotations.Elements)
            {
                if (AsDictionary(item) is { } annotation)
                {
                    annotation.Elements["/P"] = page.Reference!;
                }
            }
        }
    }
}

using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfPro.Operations.Signing;

/// <summary>
/// The changes a certification signature allows (ISO 32000-1 12.8.2.2, "DocMDP"): 1 none, 2 filling in forms and
/// signing, 3 also annotations. Read from the catalog's /Perms /DocMDP signature, its /Reference with
/// /TransformMethod /DocMDP and /TransformParams /P (2 when missing).
/// </summary>
public static class DocMdp
{
    /// <summary>No change at all is allowed after the certification.</summary>
    public const int NoChanges = 1;

    /// <summary>Filling in forms and signing are allowed.</summary>
    public const int FormsAndSigning = 2;

    /// <summary>Annotations are allowed too.</summary>
    public const int Annotations = 3;

    /// <summary>The permission of the document's certification signature, or null when it is not certified.</summary>
    public static int? PermissionOf(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Resolve(document.Internals.Catalog.Elements["/Perms"]) is not PdfDictionary perms
            || Resolve(perms.Elements["/DocMDP"]) is not PdfDictionary signature)
        {
            return null;
        }

        if (Resolve(signature.Elements["/Reference"]) is PdfArray references)
        {
            foreach (var item in references.Elements)
            {
                if (Resolve(item) is PdfDictionary reference
                    && Resolve(reference.Elements["/TransformMethod"]) is PdfName { Value: "/DocMDP" }
                    && Resolve(reference.Elements["/TransformParams"]) is PdfDictionary parameters)
                {
                    return Resolve(parameters.Elements["/P"]) is PdfInteger { Value: >= NoChanges and <= Annotations } p ? p.Value : FormsAndSigning;
                }
            }
        }

        // Certified without saying how: the default of the standard.
        return FormsAndSigning;
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
}

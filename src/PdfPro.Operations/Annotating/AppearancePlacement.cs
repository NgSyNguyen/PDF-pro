using PdfPro.Core.Geometry;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Annotating;

/// <summary>
/// Drawing an annotation's appearance into the page content (flattening comments and form fields): which appearance
/// stream is shown, and where it goes on the page.
/// </summary>
internal static class AppearancePlacement
{
    /// <summary>/AP /N: the stream itself, or the one of the state named by /AS.</summary>
    public static PdfDictionary? Normal(PdfDictionary annotation)
    {
        if (AsDictionary(annotation.Elements["/AP"]) is not { } appearances || AsDictionary(appearances.Elements["/N"]) is not { } normal)
        {
            return null;
        }

        if (normal.Stream is not null)
        {
            return normal;
        }

        return Name(annotation, "/AS") is { } state && AsDictionary(normal.Elements[state]) is { Stream: not null } chosen ? chosen : null;
    }

    /// <summary>
    /// Where the appearance goes (12.5.5): its /BBox, transformed by its /Matrix, is fitted to the annotation's /Rect.
    /// The form's /Matrix itself is applied by the Do operator.
    /// </summary>
    public static FormPlacement? Place(PdfDocument document, PdfDictionary form, PdfRect rect)
    {
        if (AsArray(form.Elements["/BBox"]) is not { Elements.Count: 4 } box || (rect.Width <= 0 && rect.Height <= 0))
        {
            return null;
        }

        var matrix = AsArray(form.Elements["/Matrix"]) is { Elements.Count: 6 } m
            ? new PdfMatrix(Number(m.Elements[0]), Number(m.Elements[1]), Number(m.Elements[2]), Number(m.Elements[3]), Number(m.Elements[4]), Number(m.Elements[5]))
            : PdfMatrix.Identity;
        var drawn = matrix.TransformBounds(Rectangle(box));
        var scaleX = drawn.Width > 0 ? rect.Width / drawn.Width : 1;
        var scaleY = drawn.Height > 0 ? rect.Height / drawn.Height : 1;
        var fit = PdfMatrix.Translation(-drawn.Left, -drawn.Bottom)
            .Then(PdfMatrix.Scaling(scaleX, scaleY))
            .Then(PdfMatrix.Translation(rect.Left, rect.Bottom));

        // Appearance streams are form XObjects (12.5.5); some writers leave the entries out.
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        if (form.Reference is null)
        {
            document.Internals.AddObject(form);
        }

        return new FormPlacement(form.Reference!, fit);
    }

    public static PdfRect Rectangle(PdfArray array)
        => new(Number(array.Elements[0]), Number(array.Elements[1]), Number(array.Elements[2]), Number(array.Elements[3]));

    private static double Number(PdfItem item) => Resolve(item) switch
    {
        PdfReal real => real.Value,
        PdfInteger integer => integer.Value,
        PdfLongInteger integer => integer.Value,
        _ => 0,
    };
}

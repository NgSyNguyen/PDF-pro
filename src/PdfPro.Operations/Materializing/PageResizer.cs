using System.Globalization;
using System.Text;
using PdfPro.Core.Geometry;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Materializing;

/// <summary>A uniform scale then a translation, in PDF user space: (x, y) → (s·x + e, s·y + f).</summary>
internal readonly record struct PageTransform(double Scale, double OffsetX, double OffsetY)
{
    public (double X, double Y) Apply(double x, double y) => ((Scale * x) + OffsetX, (Scale * y) + OffsetY);
}

/// <summary>
/// Gives a page a new size (Đổi khổ giấy): the page becomes a /MediaBox of the new size and its content is drawn
/// through a transform that fits (or centres) what was visible, clipped to it. Content streams are not rewritten, so
/// text stays text and can still be selected; annotation geometry follows the transform.
/// </summary>
internal static class PageResizer
{
    private static readonly string[] OtherBoxes = ["/CropBox", "/BleedBox", "/TrimBox", "/ArtBox"];

    /// <summary>Resizes <paramref name="page"/> and returns how its old coordinates map to the new ones.</summary>
    /// <param name="page">A page of a document opened for modification.</param>
    /// <param name="displayed">The new size as displayed (after the page's own /Rotate), in points.</param>
    /// <param name="scaleToFit">Scale the content to fit (keeping its proportions); otherwise keep its size.</param>
    public static PageTransform Resize(PdfPage page, PdfSize displayed, bool scaleToFit)
    {
        ArgumentNullException.ThrowIfNull(page);
        var rotation = PageBoxes.Rotation(page);
        var visible = PageBoxes.VisibleBox(page);
        var (width, height) = rotation % 180 == 0 ? (displayed.Width, displayed.Height) : (displayed.Height, displayed.Width);
        var scale = scaleToFit ? Math.Min(width / visible.Width, height / visible.Height) : 1;
        var transform = new PageTransform(
            scale,
            ((width - (visible.Width * scale)) / 2) - (visible.Left * scale),
            ((height - (visible.Height * scale)) / 2) - (visible.Bottom * scale));

        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(width, height));
        foreach (var box in OtherBoxes)
        {
            page.Elements.Remove(box);
        }

        WrapContent(page, transform, visible);
        if (Annotations(page) is { } annotations)
        {
            foreach (var item in annotations.Elements)
            {
                if (AsDictionary(item) is { } annotation)
                {
                    TransformAnnotation(annotation, transform);
                }
            }
        }

        return transform;
    }

    /// <summary>
    /// Puts the page's content streams between "q transform clip" and "Q" in a /Contents array of its own (the
    /// streams themselves may be shared with other pages and are left alone).
    /// </summary>
    private static void WrapContent(PdfPage page, PageTransform transform, PdfRect visible)
    {
        var document = page.Owner;
        var streams = new List<PdfItem>();
        switch (page.Elements["/Contents"])
        {
            case PdfReference reference when reference.Value is PdfArray array:
                streams.AddRange(array.Elements);
                break;
            case PdfArray array:
                streams.AddRange(array.Elements);
                break;
            case PdfReference reference:
                streams.Add(reference);
                break;
        }

        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"q {Number(transform.Scale)} 0 0 {Number(transform.Scale)} {Number(transform.OffsetX)} {Number(transform.OffsetY)} cm "
            + $"{Number(visible.Left)} {Number(visible.Bottom)} {Number(visible.Width)} {Number(visible.Height)} re W n\n");
        var contents = new PdfArray(document);
        contents.Elements.Add(NewStream(document, prefix));
        streams.ForEach(contents.Elements.Add);
        contents.Elements.Add(NewStream(document, "\nQ\n"));
        page.Elements["/Contents"] = contents;
    }

    private static PdfReference NewStream(PdfDocument document, string text)
    {
        var content = new PdfContent(document);
        content.CreateStream(Encoding.ASCII.GetBytes(text));
        document.Internals.AddObject(content);
        return content.Reference!;
    }

    /// <summary>Moves the annotation's rectangle and point lists with the page content.</summary>
    private static void TransformAnnotation(PdfDictionary annotation, PageTransform transform)
    {
        if (AsArray(annotation.Elements["/Rect"]) is { Elements.Count: 4 } rect && Numbers(rect) is { } r)
        {
            var (x1, y1) = transform.Apply(r[0], r[1]);
            var (x2, y2) = transform.Apply(r[2], r[3]);
            SetNumbers(rect, [Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2)]);
        }

        foreach (var key in new[] { "/QuadPoints", "/Vertices", "/L", "/CL" })
        {
            if (AsArray(annotation.Elements[key]) is { } points)
            {
                TransformPoints(points, transform);
            }
        }

        if (AsArray(annotation.Elements["/InkList"]) is { } ink)
        {
            foreach (var stroke in ink.Elements)
            {
                if (AsArray(stroke) is { } points)
                {
                    TransformPoints(points, transform);
                }
            }
        }

        if (AsArray(annotation.Elements["/RD"]) is { Elements.Count: 4 } differences && Numbers(differences) is { } d)
        {
            SetNumbers(differences, [.. d.Select(v => v * transform.Scale)]);
        }
    }

    private static void TransformPoints(PdfArray points, PageTransform transform)
    {
        if (points.Elements.Count % 2 != 0 || Numbers(points) is not { } values)
        {
            return;
        }

        for (var i = 0; i < values.Length; i += 2)
        {
            (values[i], values[i + 1]) = transform.Apply(values[i], values[i + 1]);
        }

        SetNumbers(points, values);
    }

    /// <summary>The array's numbers, or null when one element is not a number.</summary>
    internal static double[]? Numbers(PdfArray array)
    {
        var values = new double[array.Elements.Count];
        for (var i = 0; i < values.Length; i++)
        {
            switch (Resolve(array.Elements[i]))
            {
                case PdfInteger integer:
                    values[i] = integer.Value;
                    break;
                case PdfReal real:
                    values[i] = real.Value;
                    break;
                default:
                    return null;
            }
        }

        return values;
    }

    internal static void SetNumbers(PdfArray array, IReadOnlyList<double> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            array.Elements[i] = new PdfReal(Math.Round(values[i], 4));
        }
    }

    private static string Number(double value) => Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);
}

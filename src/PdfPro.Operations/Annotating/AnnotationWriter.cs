using System.Globalization;
using System.Text;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Stamping;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Annotating;

/// <summary>
/// Writes annotations into a page as the PDF standard has them (12.5): one dictionary per annotation with every entry
/// viewers need to show, edit and list it (/QuadPoints, /InkList, /L, /Vertices, /DA…), and an appearance stream
/// (/AP) so that every viewer shows it alike. Vector appearances are written in the page's PDF space (/BBox = /Rect);
/// text is drawn with an embedded Unicode font (Vietnamese) through PDFsharp and placed upright for the page's
/// rotation. Replies and review states are /Text annotations pointing at their comment (/IRT) with an empty
/// appearance, as Acrobat writes them: they appear in the comment's thread, not on the page.
/// </summary>
public static class AnnotationWriter
{
    private const int Print = 4;
    private const int NoZoom = 8;
    private const int NoRotate = 16;
    private const double TextPadding = 4;

    /// <summary>Appends <paramref name="items"/> (with their replies and review states) to the page's annotations.</summary>
    /// <exception cref="ArgumentException">An annotation is not valid.</exception>
    public static void Write(PdfDocument document, PdfPage page, IEnumerable<AnnotationItem> items)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        foreach (var item in list)
        {
            if (item.Validate().FirstOrDefault() is { } problem)
            {
                throw new ArgumentException($"Annotation {item.Id}: {problem}", nameof(items));
            }
        }

        var annotations = OwnAnnotations(document, page);
        foreach (var item in list)
        {
            var annotation = Annotation(document, page, item);
            annotations.Elements.Add(annotation.Reference!);
            foreach (var reply in item.Replies)
            {
                annotations.Elements.Add(Thread(document, page, annotation, item.Bounds, reply.Id, reply.Author, reply.Modified, reply.Contents, state: null).Reference!);
            }

            foreach (var review in item.Reviews)
            {
                annotations.Elements.Add(Thread(document, page, annotation, item.Bounds, review.Id, review.Author, review.Modified, string.Empty, review.State).Reference!);
            }
        }
    }

    /// <summary>
    /// Adds an answer — or, with <paramref name="state"/>, a review state — to an annotation already in the page (a
    /// comment of the file), as Acrobat does: a /Text annotation pointing at it (/IRT) that draws nothing.
    /// </summary>
    /// <param name="parent">The annotation answered; it must be an indirect object.</param>
    /// <param name="bounds">Where the answer is (the answered annotation's place).</param>
    internal static void AddToThread(PdfDocument document, PdfPage page, PdfDictionary parent, PdfRect bounds, string id, string author, DateTimeOffset when, string contents, ReviewState? state)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.Reference is null)
        {
            throw new ArgumentException("The answered annotation must be an indirect object.", nameof(parent));
        }

        OwnAnnotations(document, page).Elements.Add(Thread(document, page, parent, bounds, id, author, when, contents, state).Reference!);
    }

    /// <summary>The page's /Annots as an array of its own: /Annots may be an indirect array other pages share.</summary>
    internal static PdfArray OwnAnnotations(PdfDocument document, PdfPage page)
    {
        var annotations = new PdfArray(document);
        foreach (var existing in Annotations(page)?.Elements ?? Enumerable.Empty<PdfItem>())
        {
            annotations.Elements.Add(existing);
        }

        page.Elements["/Annots"] = annotations;
        return annotations;
    }

    private static PdfDictionary Annotation(PdfDocument document, PdfPage page, AnnotationItem item)
    {
        var (subtype, rect, flags) = item switch
        {
            MarkupAnnotation m => (SubtypeOf(m.Kind), Expand(m.Bounds, 1), Print),
            NoteAnnotation n => ("/Text", n.Bounds, Print | NoZoom | NoRotate),
            FreeTextAnnotation f => ("/FreeText", Expand(f.Bounds, (f.Callout.Count > 0 ? ArrowLength(1) : 0) + 1), Print),
            InkAnnotation i => ("/Ink", Expand(i.Bounds, i.Width), Print),
            ShapeAnnotation s => (s.Shape == NoteShape.Circle ? "/Circle" : "/Square", s.Box, Print),
            LineAnnotation l => ("/Line", Expand(l.Bounds, ArrowLength(l.Width) + l.Width), Print),
            PolyAnnotation p => (p.Closed ? "/Polygon" : "/PolyLine", Expand(p.Bounds, p.Width), Print),
            StampAnnotation s => ("/Stamp", s.Box, Print),
            _ => throw new ArgumentException($"Unknown annotation {item.GetType().Name}.", nameof(item)),
        };

        var annotation = Base(document, page, subtype, rect, flags, item.Id, item.Author, item.Created, item.Modified, item.Contents);
        annotation.Elements["/C"] = Color(document, item.Color);
        annotation.Elements["/CA"] = new PdfReal(Round(item.Opacity));
        var content = new Content();
        var resources = new PdfDictionary(document);
        switch (item)
        {
            case MarkupAnnotation markup:
                annotation.Elements["/QuadPoints"] = Numbers(document, markup.Quads.SelectMany(q => new[] { q.UpperLeft, q.UpperRight, q.LowerLeft, q.LowerRight }).SelectMany(p => new[] { p.X, p.Y }));
                Opacity(document, resources, markup.Opacity, multiply: markup.Kind == MarkupKind.Highlight);
                DrawMarkup(content, markup);
                break;
            case NoteAnnotation note:
                annotation.Elements.SetName("/Name", "/Comment");
                annotation.Elements.SetBoolean("/Open", false);
                Opacity(document, resources, note.Opacity, multiply: false);
                DrawNote(content, note);
                break;
            case FreeTextAnnotation freeText:
                WriteFreeText(document, annotation, resources, content, freeText);
                break;
            case InkAnnotation ink:
                var inkList = new PdfArray(document);
                foreach (var stroke in ink.Strokes)
                {
                    inkList.Elements.Add(Numbers(document, stroke.SelectMany(p => new[] { p.X, p.Y })));
                }

                annotation.Elements["/InkList"] = inkList;
                annotation.Elements["/BS"] = Border(document, ink.Width);
                Opacity(document, resources, ink.Opacity, multiply: false);
                DrawInk(content, ink);
                break;
            case ShapeAnnotation shape:
                annotation.Elements["/BS"] = Border(document, shape.Width);
                if (shape.Fill is { } shapeFill)
                {
                    annotation.Elements["/IC"] = Color(document, shapeFill);
                }

                Opacity(document, resources, shape.Opacity, multiply: false);
                DrawShape(content, shape);
                break;
            case LineAnnotation line:
                annotation.Elements["/L"] = Numbers(document, [line.Start.X, line.Start.Y, line.End.X, line.End.Y]);
                annotation.Elements["/LE"] = Names(document, [EndingName(line.StartEnding), EndingName(line.EndEnding)]);
                annotation.Elements["/BS"] = Border(document, line.Width);
                if (line.StartEnding == LineEnding.ClosedArrow || line.EndEnding == LineEnding.ClosedArrow)
                {
                    annotation.Elements["/IC"] = Color(document, line.Color);
                }

                Opacity(document, resources, line.Opacity, multiply: false);
                DrawLine(content, line.Color, line.Start, line.End, line.Width, line.StartEnding, line.EndEnding);
                break;
            case PolyAnnotation poly:
                annotation.Elements["/Vertices"] = Numbers(document, poly.Vertices.SelectMany(p => new[] { p.X, p.Y }));
                annotation.Elements["/BS"] = Border(document, poly.Width);
                if (poly.Fill is { } polyFill)
                {
                    annotation.Elements["/IC"] = Color(document, polyFill);
                }

                Opacity(document, resources, poly.Opacity, multiply: false);
                DrawPoly(content, poly);
                break;
            case StampAnnotation stamp:
                annotation.Elements.SetName("/Name", "/Custom");
                Opacity(document, resources, stamp.Opacity, multiply: false);
                DrawStamp(document, resources, content, stamp);
                break;
        }

        annotation.Elements["/AP"] = Appearance(document, Form(document, rect, content.ToString(), resources));
        return annotation;
    }

    /// <summary>The entries every annotation PDF_Pro writes has.</summary>
    private static PdfDictionary Base(PdfDocument document, PdfPage page, string subtype, PdfRect rect, int flags, string id, string author, DateTimeOffset created, DateTimeOffset modified, string contents)
    {
        var annotation = new PdfDictionary(document);
        document.Internals.AddObject(annotation);
        annotation.Elements.SetName("/Type", "/Annot");
        annotation.Elements.SetName("/Subtype", subtype);
        annotation.Elements["/Rect"] = Numbers(document, [rect.Left, rect.Bottom, rect.Right, rect.Top]);
        annotation.Elements.SetInteger("/F", flags);
        if (page.Reference is { } pageReference)
        {
            annotation.Elements["/P"] = pageReference;
        }

        annotation.Elements["/NM"] = Text(id);
        if (author.Length > 0)
        {
            annotation.Elements["/T"] = Text(author);
        }

        annotation.Elements["/CreationDate"] = new PdfString(Core.Documents.PdfDate.Format(created));
        annotation.Elements["/M"] = new PdfString(Core.Documents.PdfDate.Format(modified));
        if (contents.Length > 0)
        {
            annotation.Elements["/Contents"] = Text(TextNormalizer.ToNfc(contents));
        }

        return annotation;
    }

    /// <summary>A reply or a review state: a /Text annotation in the comment's thread, drawing nothing on the page.</summary>
    private static PdfDictionary Thread(PdfDocument document, PdfPage page, PdfDictionary parent, PdfRect bounds, string id, string author, DateTimeOffset modified, string contents, ReviewState? state)
    {
        var text = state is { } review ? $"{review} set by {author}" : contents;
        var entry = Base(document, page, "/Text", bounds, Print | NoZoom | NoRotate, id, author, modified, modified, text);
        entry.Elements["/IRT"] = parent.Reference;
        entry.Elements.SetName("/RT", "/R");
        entry.Elements.SetName("/Name", "/Comment");
        if (state is { } value)
        {
            entry.Elements.SetName("/State", "/" + value);
            entry.Elements.SetName("/StateModel", "/Review");
        }

        entry.Elements["/AP"] = Appearance(document, Form(document, new PdfRect(bounds.Left, bounds.Bottom, bounds.Left + 1, bounds.Bottom + 1), string.Empty, null));
        return entry;
    }

    private static void WriteFreeText(PdfDocument document, PdfDictionary annotation, PdfDictionary resources, Content content, FreeTextAnnotation freeText)
    {
        var style = freeText.Style;
        // As Acrobat has it: /C is the background (none when absent), the border colour goes with the text's in /DA.
        annotation.Elements.Remove("/C");
        if (freeText.Fill is { } background)
        {
            annotation.Elements["/C"] = Color(document, background);
        }

        var borderColor = freeText.Border is { } stroke ? $"{Rgb(stroke)} RG " : string.Empty;
        annotation.Elements["/DA"] = new PdfString(string.Create(CultureInfo.InvariantCulture, $"{borderColor}/Helv {Round(style.FontSize)} Tf {Rgb(style.Color)} rg"));
        annotation.Elements["/DS"] = new PdfString(string.Create(CultureInfo.InvariantCulture, $"font: {style.FontFamily} {Round(style.FontSize)}pt; color:{style.Color.ToHex()}"));
        annotation.Elements["/BS"] = Border(document, freeText.Border is null ? 0 : 1);
        if (freeText.Callout.Count > 0)
        {
            annotation.Elements.SetName("/IT", "/FreeTextCallout");
            annotation.Elements["/CL"] = Numbers(document, freeText.Callout.SelectMany(p => new[] { p.X, p.Y }));
            annotation.Elements.SetName("/LE", "/OpenArrow");
            var rect = Expand(freeText.Bounds, ArrowLength(1) + 1);
            annotation.Elements["/RD"] = Numbers(document, [freeText.Box.Left - rect.Left, freeText.Box.Bottom - rect.Bottom, rect.Right - freeText.Box.Right, rect.Top - freeText.Box.Top]);
        }

        Opacity(document, resources, freeText.Opacity, multiply: false);
        var box = freeText.Box;
        if (freeText.Fill is { } fill)
        {
            content.Fill(fill).Rect(box).Raw("f");
        }

        if (freeText.Border is { } border)
        {
            content.Stroke(border).Width(1).Rect(Inset(box, 0.5)).Raw("S");
        }

        if (freeText.Callout.Count > 1)
        {
            var points = freeText.Callout;
            content.Stroke(freeText.Color).Width(1).Raw("1 J 1 j");
            content.MoveTo(points[0]);
            foreach (var point in points.Skip(1))
            {
                content.LineTo(point);
            }

            content.Raw("S");
            OpenArrow(content, points[0], points[1], 1, freeText.Color);
        }

        // The text: laid out upright for the page's rotation inside the box, less the padding.
        var mapper = new PageCoordinateMapper(box, freeText.Rotation, 1);
        var shown = freeText.Rotation is 90 or 270 ? new PdfSize(box.Height, box.Width) : box.Size;
        var metrics = FontLibrary.Instance.Metrics(style.FontFamily, style.Bold, style.Italic);
        var layout = TextLayout.Layout(freeText.Contents, metrics, style.FontSize, Math.Max(1, shown.Width - (2 * TextPadding)), style.Alignment, style.LineSpacing);
        var text = StampForms.Draw(document, shown, style.FontSize, g => OverlayPainter.DrawLayout(g, layout, OverlayPainter.Font(style), new XSolidBrush(OverlayPainter.Color(style.Color)), new XPoint(TextPadding, TextPadding)));
        PlaceForm(document, resources, content, "/Fx0", text.Form, text.FormToLocal.Then(mapper.ScreenToPdfMatrix));
    }

    private static void DrawStamp(PdfDocument document, PdfDictionary resources, Content content, StampAnnotation stamp)
    {
        var mapper = new PageCoordinateMapper(stamp.Box, stamp.Rotation, 1);
        var shown = stamp.Rotation is 90 or 270 ? new PdfSize(stamp.Box.Height, stamp.Box.Width) : stamp.Box.Size;
        StampForm form;
        if (stamp.ImagePath is { } image)
        {
            form = StampForms.Draw(document, shown, 0, g => OverlayPainter.DrawImage(g, image, shown));
        }
        else
        {
            var lines = TextNormalizer.ToNfc(stamp.Text!).Split('\n');
            var color = new XSolidBrush(OverlayPainter.Color(stamp.Color));
            var pen = new XPen(OverlayPainter.Color(stamp.Color), 2);
            form = StampForms.Draw(document, shown, 2, g =>
            {
                g.DrawRoundedRectangle(pen, 1, 1, shown.Width - 2, shown.Height - 2, 8, 8);
                var size = Math.Min(shown.Height * 0.8 / lines.Length, 40);
                var style = new TextStyle(FontFamilies.Default, size, stamp.Color, Bold: true);
                var font = OverlayPainter.Font(style);
                var metrics = FontLibrary.Instance.Metrics(style.FontFamily, bold: true, italic: false);
                var widest = lines.Max(l => TextLayout.Measure(l, metrics, size));
                if (widest > shown.Width - 12)
                {
                    size *= (shown.Width - 12) / widest; // shrink long words to fit the frame
                    style = style with { FontSize = size };
                    font = OverlayPainter.Font(style);
                }

                var lineHeight = size * 1.15;
                var top = (shown.Height - (lineHeight * lines.Length)) / 2;
                for (var i = 0; i < lines.Length; i++)
                {
                    g.DrawString(lines[i], font, color, new XRect(0, top + (i * lineHeight), shown.Width, lineHeight), XStringFormats.Center);
                }
            });
        }

        PlaceForm(document, resources, content, "/Fx0", form.Form, form.FormToLocal.Then(mapper.ScreenToPdfMatrix));
    }

    private static void DrawMarkup(Content content, MarkupAnnotation markup)
    {
        foreach (var quad in markup.Quads)
        {
            var up = Unit(quad.LowerLeft, quad.UpperLeft);
            var height = Distance(quad.LowerLeft, quad.UpperLeft);
            switch (markup.Kind)
            {
                case MarkupKind.Highlight:
                    content.Fill(markup.Color).MoveTo(quad.UpperLeft).LineTo(quad.UpperRight).LineTo(quad.LowerRight).LineTo(quad.LowerLeft).Raw("h f");
                    break;
                case MarkupKind.Underline:
                case MarkupKind.StrikeOut:
                    var width = Math.Max(0.5, height / 14);
                    var offset = markup.Kind == MarkupKind.Underline ? width : height * 0.5;
                    content.Stroke(markup.Color).Width(width)
                        .MoveTo(Offset(quad.LowerLeft, up, offset)).LineTo(Offset(quad.LowerRight, up, offset)).Raw("S");
                    break;
                case MarkupKind.Squiggly:
                    var along = Unit(quad.LowerLeft, quad.LowerRight);
                    var length = Distance(quad.LowerLeft, quad.LowerRight);
                    var step = Math.Max(1, height / 6);
                    var amplitude = Math.Max(0.75, height / 12);
                    content.Stroke(markup.Color).Width(Math.Max(0.5, height / 18)).MoveTo(quad.LowerLeft);
                    for (var (x, high) = (step, true); x <= length + 0.01; x += step, high = !high)
                    {
                        content.LineTo(Offset(Offset(quad.LowerLeft, along, Math.Min(x, length)), up, high ? amplitude : 0));
                    }

                    content.Raw("S");
                    break;
            }
        }
    }

    /// <summary>A speech bubble with three lines of "text", in the note's colour.</summary>
    private static void DrawNote(Content content, NoteAnnotation note)
    {
        var (l, b, r, t) = (note.Bounds.Left, note.Bounds.Bottom, note.Bounds.Right, note.Bounds.Top);
        content.Fill(note.Color).Stroke(new RgbColor(64, 64, 64)).Width(1).Raw("1 j")
            .MoveTo(new PdfPoint(l + 1.5, t - 1.5)).LineTo(new PdfPoint(r - 1.5, t - 1.5)).LineTo(new PdfPoint(r - 1.5, b + 6))
            .LineTo(new PdfPoint(l + 9, b + 6)).LineTo(new PdfPoint(l + 4, b + 1.5)).LineTo(new PdfPoint(l + 5, b + 6))
            .LineTo(new PdfPoint(l + 1.5, b + 6)).Raw("h B");
        content.Stroke(new RgbColor(64, 64, 64)).Width(1);
        foreach (var y in new[] { t - 6, t - 9.5, t - 13 })
        {
            content.MoveTo(new PdfPoint(l + 4.5, y)).LineTo(new PdfPoint(r - 4.5, y));
        }

        content.Raw("S");
    }

    private static void DrawInk(Content content, InkAnnotation ink)
    {
        content.Stroke(ink.Color).Width(ink.Width).Raw("1 J 1 j");
        foreach (var stroke in ink.Strokes)
        {
            content.MoveTo(stroke[0]);
            foreach (var point in stroke.Count == 1 ? stroke : stroke.Skip(1))
            {
                content.LineTo(point);
            }
        }

        content.Raw("S");
    }

    private static void DrawShape(Content content, ShapeAnnotation shape)
    {
        var box = Inset(shape.Box, shape.Width / 2);
        content.Stroke(shape.Color).Width(shape.Width);
        if (shape.Fill is { } fill)
        {
            content.Fill(fill);
        }

        if (shape.Shape == NoteShape.Circle)
        {
            Ellipse(content, box);
        }
        else
        {
            content.Rect(box);
        }

        content.Raw(shape.Fill is null ? "S" : "B");
    }

    private static void DrawPoly(Content content, PolyAnnotation poly)
    {
        content.Stroke(poly.Color).Width(poly.Width).Raw("1 j");
        if (poly.Fill is { } fill)
        {
            content.Fill(fill);
        }

        content.MoveTo(poly.Vertices[0]);
        foreach (var vertex in poly.Vertices.Skip(1))
        {
            content.LineTo(vertex);
        }

        content.Raw(poly.Closed ? (poly.Fill is null ? "s" : "b") : "S");
    }

    private static void DrawLine(Content content, RgbColor color, PdfPoint start, PdfPoint end, double width, LineEnding startEnding, LineEnding endEnding)
    {
        var head = Math.Min(ArrowLength(width), Distance(start, end) / 2);
        var direction = Unit(start, end);
        var from = startEnding == LineEnding.ClosedArrow ? Offset(start, direction, head) : start;
        var to = endEnding == LineEnding.ClosedArrow ? Offset(end, direction, -head) : end;
        content.Stroke(color).Width(width).Raw("1 J 1 j").MoveTo(from).LineTo(to).Raw("S");
        Ending(content, start, end, width, startEnding, color);
        Ending(content, end, start, width, endEnding, color);
    }

    /// <summary>An arrow head at <paramref name="tip"/> for a line coming from <paramref name="from"/>.</summary>
    private static void Ending(Content content, PdfPoint tip, PdfPoint from, double width, LineEnding ending, RgbColor color)
    {
        if (ending == LineEnding.OpenArrow)
        {
            OpenArrow(content, tip, from, width, color);
        }
        else if (ending == LineEnding.ClosedArrow)
        {
            var (left, right) = ArrowWings(tip, from, width);
            content.Fill(color).MoveTo(tip).LineTo(left).LineTo(right).Raw("h f");
        }
    }

    private static void OpenArrow(Content content, PdfPoint tip, PdfPoint from, double width, RgbColor color)
    {
        var (left, right) = ArrowWings(tip, from, width);
        content.Stroke(color).Width(width).Raw("1 J 1 j").MoveTo(left).LineTo(tip).LineTo(right).Raw("S");
    }

    private static (PdfPoint Left, PdfPoint Right) ArrowWings(PdfPoint tip, PdfPoint from, double width)
    {
        var back = Unit(tip, from);
        var length = Math.Min(ArrowLength(width), Distance(tip, from) / 2);
        var basePoint = Offset(tip, back, length);
        var side = new PdfPoint(-back.Y, back.X);
        return (Offset(basePoint, side, length / 2), Offset(basePoint, side, -length / 2));
    }

    private static void Ellipse(Content content, PdfRect box)
    {
        const double k = 0.5523; // control point distance for a quarter circle
        var (cx, cy) = ((box.Left + box.Right) / 2, (box.Bottom + box.Top) / 2);
        var (rx, ry) = (box.Width / 2, box.Height / 2);
        content.MoveTo(new PdfPoint(cx + rx, cy))
            .CurveTo(new PdfPoint(cx + rx, cy + (k * ry)), new PdfPoint(cx + (k * rx), cy + ry), new PdfPoint(cx, cy + ry))
            .CurveTo(new PdfPoint(cx - (k * rx), cy + ry), new PdfPoint(cx - rx, cy + (k * ry)), new PdfPoint(cx - rx, cy))
            .CurveTo(new PdfPoint(cx - rx, cy - (k * ry)), new PdfPoint(cx - (k * rx), cy - ry), new PdfPoint(cx, cy - ry))
            .CurveTo(new PdfPoint(cx + (k * rx), cy - ry), new PdfPoint(cx + rx, cy - (k * ry)), new PdfPoint(cx + rx, cy))
            .Raw("h");
    }

    /// <summary>Arrow head length for a line width (as the page editor draws them).</summary>
    private static double ArrowLength(double width) => LineOverlay.ArrowLength(width);

    private static void PlaceForm(PdfDocument document, PdfDictionary resources, Content content, string name, PdfSharp.Pdf.Advanced.PdfReference form, Core.Geometry.PdfMatrix matrix)
    {
        var objects = new PdfDictionary(document);
        objects.Elements[name] = form;
        resources.Elements["/XObject"] = objects;
        content.Raw("q").Raw(string.Create(CultureInfo.InvariantCulture, $"{Round(matrix.A)} {Round(matrix.B)} {Round(matrix.C)} {Round(matrix.D)} {Round(matrix.E)} {Round(matrix.F)} cm")).Raw($"{name} Do").Raw("Q");
    }

    /// <summary>The graphics state the appearance starts with: its opacity, and multiplying colours for highlights (text stays readable).</summary>
    private static void Opacity(PdfDocument document, PdfDictionary resources, double opacity, bool multiply)
    {
        var state = new PdfDictionary(document);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements["/CA"] = new PdfReal(Round(opacity));
        state.Elements["/ca"] = new PdfReal(Round(opacity));
        if (multiply)
        {
            state.Elements.SetName("/BM", "/Multiply");
        }

        var states = new PdfDictionary(document);
        states.Elements["/GS0"] = state;
        resources.Elements["/ExtGState"] = states;
    }

    private static PdfSharp.Pdf.Advanced.PdfReference Form(PdfDocument document, PdfRect bbox, string content, PdfDictionary? resources)
    {
        var form = new PdfDictionary(document);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = Numbers(document, [bbox.Left, bbox.Bottom, bbox.Right, bbox.Top]);
        if (resources is not null)
        {
            form.Elements["/Resources"] = resources;
        }

        var prefix = resources?.Elements.ContainsKey("/ExtGState") == true ? "/GS0 gs\n" : string.Empty;
        form.CreateStream(Encoding.ASCII.GetBytes(prefix + content));
        document.Internals.AddObject(form);
        return form.Reference!;
    }

    private static PdfDictionary Appearance(PdfDocument document, PdfSharp.Pdf.Advanced.PdfReference normal)
    {
        var appearance = new PdfDictionary(document);
        appearance.Elements["/N"] = normal;
        return appearance;
    }

    private static PdfDictionary Border(PdfDocument document, double width)
    {
        var border = new PdfDictionary(document);
        border.Elements["/W"] = new PdfReal(Round(width));
        border.Elements.SetName("/S", "/S");
        return border;
    }

    private static string SubtypeOf(MarkupKind kind) => kind switch
    {
        MarkupKind.Underline => "/Underline",
        MarkupKind.StrikeOut => "/StrikeOut",
        MarkupKind.Squiggly => "/Squiggly",
        _ => "/Highlight",
    };

    private static string EndingName(LineEnding ending) => ending switch
    {
        LineEnding.OpenArrow => "/OpenArrow",
        LineEnding.ClosedArrow => "/ClosedArrow",
        _ => "/None",
    };

    private static PdfArray Numbers(PdfDocument document, IEnumerable<double> values)
    {
        var array = new PdfArray(document);
        foreach (var value in values)
        {
            array.Elements.Add(new PdfReal(Round(value)));
        }

        return array;
    }

    private static PdfArray Names(PdfDocument document, IEnumerable<string> names)
    {
        var array = new PdfArray(document);
        foreach (var name in names)
        {
            array.Elements.Add(new PdfName(name));
        }

        return array;
    }

    private static PdfArray Color(PdfDocument document, RgbColor color)
        => Numbers(document, [color.R / 255.0, color.G / 255.0, color.B / 255.0]);

    private static PdfString Text(string text)
        => text.All(c => c < 128) ? new PdfString(text) : new PdfString(text, PdfStringEncoding.Unicode);

    private static string Rgb(RgbColor color)
        => string.Create(CultureInfo.InvariantCulture, $"{Round(color.R / 255.0)} {Round(color.G / 255.0)} {Round(color.B / 255.0)}");

    private static PdfRect Expand(PdfRect rect, double by) => new(rect.Left - by, rect.Bottom - by, rect.Right + by, rect.Top + by);

    private static PdfRect Inset(PdfRect rect, double by)
        => rect.Width > 2 * by && rect.Height > 2 * by ? new PdfRect(rect.Left + by, rect.Bottom + by, rect.Right - by, rect.Top - by) : rect;

    private static PdfPoint Unit(PdfPoint from, PdfPoint to)
    {
        var length = Distance(from, to);
        return length <= 0 ? new PdfPoint(1, 0) : new PdfPoint((to.X - from.X) / length, (to.Y - from.Y) / length);
    }

    private static double Distance(PdfPoint a, PdfPoint b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static PdfPoint Offset(PdfPoint point, PdfPoint direction, double by) => new(point.X + (direction.X * by), point.Y + (direction.Y * by));

    private static double Round(double value) => Math.Round(value, 4);

    /// <summary>A content stream written with invariant numbers.</summary>
    private sealed class Content
    {
        private readonly StringBuilder _builder = new();

        public Content Raw(string operators)
        {
            _builder.Append(operators).Append('\n');
            return this;
        }

        public Content Fill(RgbColor color) => Raw($"{Rgb(color)} rg");

        public Content Stroke(RgbColor color) => Raw($"{Rgb(color)} RG");

        public Content Width(double width) => Raw(string.Create(CultureInfo.InvariantCulture, $"{Round(width)} w"));

        public Content MoveTo(PdfPoint point) => Raw(string.Create(CultureInfo.InvariantCulture, $"{Round(point.X)} {Round(point.Y)} m"));

        public Content LineTo(PdfPoint point) => Raw(string.Create(CultureInfo.InvariantCulture, $"{Round(point.X)} {Round(point.Y)} l"));

        public Content CurveTo(PdfPoint first, PdfPoint second, PdfPoint end)
            => Raw(string.Create(CultureInfo.InvariantCulture, $"{Round(first.X)} {Round(first.Y)} {Round(second.X)} {Round(second.Y)} {Round(end.X)} {Round(end.Y)} c"));

        public Content Rect(PdfRect rect)
            => Raw(string.Create(CultureInfo.InvariantCulture, $"{Round(rect.Left)} {Round(rect.Bottom)} {Round(rect.Width)} {Round(rect.Height)} re"));

        public override string ToString() => _builder.ToString();
    }
}

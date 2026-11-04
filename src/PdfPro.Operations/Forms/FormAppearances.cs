using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Stamping;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Forms;

/// <summary>
/// Appearance streams of form widgets (12.7.4.3), drawn with PDFsharp: box (background and border from /MK and /BS),
/// then the value — text in Noto Sans (so Vietnamese shows in every viewer, whatever font /DA names), a check mark, a
/// radio dot, the choices of a list, a button caption. On a page turned by /Rotate the drawing is turned with it
/// (/MK /R) so that it reads upright.
/// </summary>
internal static class FormAppearances
{
    /// <summary>Room between the border and the text, in points.</summary>
    private const double Padding = 2;

    /// <summary>The font size used for "auto" (/DA size 0): what fits one line in the box, at most this.</summary>
    private const double MaxAutoSize = 12;

    private static readonly XColor Border = XColor.FromArgb(128, 128, 128);
    private static readonly XColor ListSelection = XColor.FromArgb(153, 193, 218);
    private static readonly XColor ButtonFace = XColor.FromArgb(230, 230, 230);

    /// <summary>Draws the widget's appearance for the field's current value and stores it in /AP (and /AS).</summary>
    public static void Refresh(PdfDocument document, FormField field, PdfDictionary widget, int rotation)
    {
        var rect = FormReader.Rect(widget);
        var upright = rotation is 90 or 270 ? new PdfSize(rect.Height, rect.Width) : new PdfSize(rect.Width, rect.Height);
        if (upright.Width <= 0 || upright.Height <= 0)
        {
            return;
        }

        var appearance = new PdfDictionary(document);
        switch (field.Kind)
        {
            case FormFieldKind.CheckBox or FormFieldKind.RadioGroup:
                var on = FormReader.OnState(widget) ?? "Yes";
                var states = new PdfDictionary(document);
                states.Elements["/" + on] = Form(document, upright, rotation, g => DrawButton(g, upright, field.Kind, true));
                states.Elements["/" + FormField.Off] = Form(document, upright, rotation, g => DrawButton(g, upright, field.Kind, false));
                appearance.Elements["/N"] = states;
                widget.Elements.SetName("/AS", "/" + (field.Value == on ? on : FormField.Off));
                break;
            default:
                appearance.Elements["/N"] = Form(document, upright, rotation, g => Draw(g, upright, field));
                break;
        }

        widget.Elements["/AP"] = appearance;
    }

    private static PdfReference Form(PdfDocument document, PdfSize size, int rotation, Action<XGraphics> draw)
    {
        var form = StampForms.Draw(document, size, 0, draw);
        if (rotation is 90 or 180 or 270 && AsDictionary(form.Form) is { } dictionary)
        {
            // Turned with the page so that it reads upright; /Rect fitting places it.
            var turn = PdfMatrix.Rotation(rotation);
            var matrix = new PdfArray(document);
            foreach (var value in new[] { turn.A, turn.B, turn.C, turn.D, 0, 0 })
            {
                matrix.Elements.Add(new PdfReal(Math.Round(value, 4)));
            }

            dictionary.Elements["/Matrix"] = matrix;
        }

        return form.Form;
    }

    private static void Draw(XGraphics graphics, PdfSize size, FormField field)
    {
        DrawBox(graphics, size, field.Kind == FormFieldKind.PushButton ? ButtonFace : XColors.White);
        switch (field.Kind)
        {
            case FormFieldKind.Text or FormFieldKind.ComboBox:
                DrawText(graphics, size, field, Shown(field));
                break;
            case FormFieldKind.ListBox:
                DrawList(graphics, size, field);
                break;
            case FormFieldKind.PushButton when field.Value is { Length: > 0 } caption:
                DrawLine(graphics, size, caption, Font(field, size), FormTextAlignment.Center, (size.Height - Font(field, size).Size * 1.2) / 2);
                break;
        }
    }

    /// <summary>What a text field or combo box shows: its value (dots for a password, the display text of a choice).</summary>
    private static string Shown(FormField field)
    {
        var value = TextNormalizer.ToNfc(field.Value ?? string.Empty);
        if (field.Flags.HasFlag(FormFieldFlags.Password))
        {
            return new string('•', value.Length);
        }

        if (field.Format is { } format)
        {
            return format.Display(value);
        }

        return field.Kind == FormFieldKind.ComboBox ? field.Options.FirstOrDefault(o => o.Export == value)?.Display ?? value : value;
    }

    private static void DrawBox(XGraphics graphics, PdfSize size, XColor background)
    {
        graphics.DrawRectangle(new XPen(Border, 1), new XSolidBrush(background), 0.5, 0.5, size.Width - 1, size.Height - 1);
    }

    private static void DrawText(XGraphics graphics, PdfSize size, FormField field, string text)
    {
        var font = Font(field, size);
        if (text.Length == 0)
        {
            // An invisible space keeps the font in the appearance's resources: its family is read back from there.
            DrawLine(graphics, size, " ", font, FormTextAlignment.Left, (size.Height - (font.Size * 1.2)) / 2);
            return;
        }
        if (field.Flags.HasFlag(FormFieldFlags.Comb) && field.MaxLength > 0)
        {
            // One character per cell.
            var cell = size.Width / field.MaxLength;
            var top = (size.Height - (font.Size * 1.2)) / 2;
            var i = 0;
            foreach (var rune in text.EnumerateRunes().Take(field.MaxLength))
            {
                var glyph = rune.ToString();
                var width = graphics.MeasureString(glyph, font).Width;
                graphics.DrawString(glyph, font, XBrushes.Black, (i * cell) + ((cell - width) / 2), top + font.Size, XStringFormats.BaseLineLeft);
                i++;
            }

            return;
        }

        if (field.Flags.HasFlag(FormFieldFlags.Multiline))
        {
            var y = Padding;
            foreach (var line in Wrap(graphics, text, font, size.Width - (2 * Padding)))
            {
                if (y + (font.Size * 1.2) > size.Height)
                {
                    break;
                }

                DrawLine(graphics, size, line, font, field.Alignment, y);
                y += font.Size * 1.2;
            }

            return;
        }

        DrawLine(graphics, size, text, font, field.Alignment, (size.Height - (font.Size * 1.2)) / 2);
    }

    private static void DrawLine(XGraphics graphics, PdfSize size, string text, XFont font, FormTextAlignment alignment, double top)
    {
        var width = graphics.MeasureString(text, font).Width;
        var x = alignment switch
        {
            FormTextAlignment.Center => (size.Width - width) / 2,
            FormTextAlignment.Right => size.Width - Padding - width,
            _ => Padding,
        };
        graphics.DrawString(text, font, XBrushes.Black, x, top + (font.Size * 0.95), XStringFormats.BaseLineLeft);
    }

    private static void DrawList(XGraphics graphics, PdfSize size, FormField field)
    {
        var font = Font(field, size, lines: 1);
        var line = font.Size * 1.2;
        var y = Padding;
        foreach (var option in field.Options)
        {
            if (y + line > size.Height)
            {
                break;
            }

            if (field.Selected.Contains(option.Export))
            {
                graphics.DrawRectangle(new XSolidBrush(ListSelection), 1, y, size.Width - 2, line);
            }

            DrawLine(graphics, size, TextNormalizer.ToNfc(option.Display), font, FormTextAlignment.Left, y);
            y += line;
        }
    }

    private static void DrawButton(XGraphics graphics, PdfSize size, FormFieldKind kind, bool on)
    {
        var side = Math.Min(size.Width, size.Height);
        var (x, y) = ((size.Width - side) / 2, (size.Height - side) / 2);
        var pen = new XPen(Border, 1);
        if (kind == FormFieldKind.RadioGroup)
        {
            graphics.DrawEllipse(pen, XBrushes.White, x + 0.5, y + 0.5, side - 1, side - 1);
            if (on)
            {
                var dot = side * 0.5;
                graphics.DrawEllipse(XBrushes.Black, x + ((side - dot) / 2), y + ((side - dot) / 2), dot, dot);
            }

            return;
        }

        graphics.DrawRectangle(pen, XBrushes.White, x + 0.5, y + 0.5, side - 1, side - 1);
        if (on)
        {
            var tick = new XPen(XColors.Black, Math.Max(1, side * 0.12)) { LineJoin = XLineJoin.Round, LineCap = XLineCap.Round };
            graphics.DrawLines(tick, [new XPoint(x + (side * 0.22), y + (side * 0.52)), new XPoint(x + (side * 0.42), y + (side * 0.72)), new XPoint(x + (side * 0.78), y + (side * 0.28))]);
        }
    }

    /// <summary>The font of the value: the field's bundled family (Noto Sans by default) at the size of /DA, or (size 0) what fits one line of the box.</summary>
    private static XFont Font(FormField field, PdfSize size, int lines = 1)
    {
        FontLibrary.Register();
        var fontSize = field.FontSize > 0
            ? field.FontSize
            : Math.Clamp((size.Height - (2 * Padding)) / (1.2 * lines), 4, MaxAutoSize);
        return new XFont(field.FontFamily is { } family && FontFamilies.IsBundled(family) ? family : FontFamilies.NotoSans, fontSize, XFontStyleEx.Regular);
    }

    private static IEnumerable<string> Wrap(XGraphics graphics, string text, XFont font, double width)
    {
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && graphics.MeasureString(candidate, font).Width > width)
                {
                    yield return line;
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            yield return line;
        }
    }
}

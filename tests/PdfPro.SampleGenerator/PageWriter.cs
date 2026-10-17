using PdfSharp.Drawing;

namespace PdfPro.SampleGenerator;

/// <summary>
/// Minimal top-down text layout on one page: headings, wrapped paragraphs, centred lines.
/// Coordinates are XGraphics coordinates (origin top-left, y down, points).
/// </summary>
internal sealed class PageWriter(XGraphics graphics, double left = 72, double right = 523, double top = 72)
{
    public static readonly XFont Body = new("Times New Roman", 12);
    public static readonly XFont BodyBold = new("Times New Roman", 12, XFontStyleEx.Bold);
    public static readonly XFont Heading1 = new("Arial", 16, XFontStyleEx.Bold);
    public static readonly XFont Heading2 = new("Arial", 13, XFontStyleEx.Bold);
    public static readonly XFont Heading3 = new("Arial", 12, XFontStyleEx.BoldItalic);
    public static readonly XFont Small = new("Arial", 9);

    public double Y { get; private set; } = top;

    public XGraphics Graphics => graphics;

    public void Space(double points) => Y += points;

    public double Heading(string text, XFont font)
    {
        Y += 6;
        var position = Y;
        graphics.DrawString(text, font, XBrushes.Black, new XPoint(left, Y + font.Height), XStringFormats.BottomLeft);
        Y += font.Height + 6;
        return position;
    }

    public void Centered(string text, XFont font, XBrush? brush = null)
    {
        graphics.DrawString(text, font, brush ?? XBrushes.Black, new XRect(left, Y, right - left, font.Height), XStringFormats.Center);
        Y += font.Height + 2;
    }

    public void Paragraph(string text, XFont? font = null, double indent = 0)
    {
        font ??= Body;
        var lineHeight = font.Height * 1.25;
        var width = right - left - indent;
        var line = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && graphics.MeasureString(candidate, font).Width > width)
            {
                DrawLine(line, font, indent);
                Y += lineHeight;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }

        if (line.Length > 0)
        {
            DrawLine(line, font, indent);
            Y += lineHeight;
        }

        Y += 4;
    }

    public void Footer(string text, double pageHeight)
        => graphics.DrawString(text, Small, XBrushes.Gray, new XRect(left, pageHeight - 40, right - left, 12), XStringFormats.Center);

    private void DrawLine(string text, XFont font, double indent)
        => graphics.DrawString(text, font, XBrushes.Black, new XPoint(left + indent, Y + font.Height), XStringFormats.BottomLeft);
}

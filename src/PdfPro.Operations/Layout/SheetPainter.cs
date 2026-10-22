using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Fonts;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Layout.PageForms;

namespace PdfPro.Operations.Layout;

/// <summary>A page drawn on a sheet: the form, where its displayed page goes, and an optional clip (posters).</summary>
/// <param name="Target">Where the whole page lands, in the sheet's PDF space.</param>
/// <param name="Clip">Only this part of the sheet shows the page; null for all of it.</param>
internal readonly record struct Placement(PageForm Page, PdfRect Target, PdfRect? Clip = null);

/// <summary>Writes sheets: new pages that draw page forms, plus thin borders, cut marks and labels.</summary>
internal static class SheetPainter
{
    private static readonly XPen Hairline = new(XColors.Gray, 0.5);
    private static readonly XPen CutMarkPen = new(XColors.Black, 0.3);

    public static PdfPage AddSheet(PdfDocument output, PdfSize size)
    {
        var sheet = output.AddPage();
        sheet.Width = XUnit.FromPoint(size.Width);
        sheet.Height = XUnit.FromPoint(size.Height);
        return sheet;
    }

    /// <summary>Draws the forms with "q [clip] cm /Name Do Q", one content stream for the whole sheet.</summary>
    public static void Draw(PdfPage sheet, IReadOnlyList<Placement> placements)
    {
        var document = sheet.Owner;
        var resources = sheet.Resources;
        if (resources.Elements["/XObject"] is not PdfDictionary forms)
        {
            forms = new PdfDictionary(document);
            resources.Elements["/XObject"] = forms;
        }

        var text = new StringBuilder();
        for (var i = 0; i < placements.Count; i++)
        {
            var (page, target, clip) = placements[i];
            var name = $"/PdfProPage{i}";
            forms.Elements[name] = page.Form;
            var scale = target.Width / page.Size.Width;
            text.Append("q ");
            if (clip is { } area)
            {
                text.Append($"{Number(area.Left)} {Number(area.Bottom)} {Number(area.Width)} {Number(area.Height)} re W n ");
            }

            text.Append($"{Number(scale)} 0 0 {Number(scale)} {Number(target.Left)} {Number(target.Bottom)} cm {name} Do Q\n");
        }

        var content = new PdfContent(document);
        content.CreateStream(Encoding.ASCII.GetBytes(text.ToString()));
        document.Internals.AddObject(content);
        sheet.Elements["/Contents"] = content.Reference!;
    }

    /// <summary>A thin frame around each rectangle (in the sheet's PDF space).</summary>
    public static void DrawBorders(PdfPage sheet, IEnumerable<PdfRect> rectangles)
    {
        var mapper = Mapper(sheet);
        using var graphics = XGraphics.FromPdfPage(sheet, XGraphicsPdfPageOptions.Append);
        foreach (var rectangle in rectangles)
        {
            var area = mapper.PdfRectToScreen(rectangle);
            graphics.DrawRectangle(Hairline, area.X, area.Y, area.Width, area.Height);
        }
    }

    /// <summary>Corner cut marks outside <paramref name="area"/> and a small label under it (posters).</summary>
    public static void DrawCutMarksAndLabel(PdfPage sheet, PdfRect area, double length, string? label)
    {
        var box = Mapper(sheet).PdfRectToScreen(area);
        using var graphics = XGraphics.FromPdfPage(sheet, XGraphicsPdfPageOptions.Append);
        if (length > 0)
        {
            foreach (var x in new[] { box.X, box.X + box.Width })
            {
                foreach (var y in new[] { box.Y, box.Y + box.Height })
                {
                    var outX = x <= box.X ? -1 : 1;
                    var outY = y <= box.Y ? -1 : 1;
                    graphics.DrawLine(CutMarkPen, x + (outX * 2), y, x + (outX * (2 + length)), y);
                    graphics.DrawLine(CutMarkPen, x, y + (outY * 2), x, y + (outY * (2 + length)));
                }
            }
        }

        if (label is not null)
        {
            BundledFonts.Register();
            var font = new XFont(BundledFonts.Family, 7, XFontStyleEx.Regular);
            graphics.DrawString(label, font, XBrushes.Gray, new XRect(box.X, box.Y + box.Height + 2, box.Width, 10), XStringFormats.TopCenter);
        }
    }

    private static Core.Geometry.PageCoordinateMapper Mapper(PdfPage sheet)
        => new(new PdfRect(0, 0, sheet.Width.Point, sheet.Height.Point), 0, 1);
}

using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// links-annots.pdf: 6 A4 pages ("Trang 1" … "Trang 6") with everything that must survive page reorganization:
/// internal links in all three forms (direct /Dest, named destination, GoTo action), a web link, a sticky note,
/// a highlight, bookmarks, page labels (i, then 1–5) and an /OpenAction. All content is fake.
/// </summary>
/// <remarks>
/// Page 1 holds the links: to page 4 (/Dest), to page 6 (named destination "phu-luc"), to page 3 (GoTo action) and to
/// https://example.com/pdfpro. Page 3 links back to page 1. Page 2 has the note and the highlight.
/// </remarks>
internal sealed class LinksAnnotsSample : ISample
{
    public const int PageCount = 6;
    public const string NamedDestination = "phu-luc";

    private const double PageHeight = 842;

    public string FileName => "links-annots.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create("Liên kết và chú thích", "Kiểm thử giữ liên kết, chú thích khi sắp xếp trang");
        var pages = Enumerable.Range(0, PageCount).Select(_ => SampleDocument.AddA4Page(document)).ToArray();
        for (var i = 0; i < PageCount; i++)
        {
            using var graphics = XGraphics.FromPdfPage(pages[i]);
            var writer = new PageWriter(graphics);
            var number = (i + 1).ToString(CultureInfo.InvariantCulture);
            writer.Heading($"Trang {number} — tài liệu liên kết mẫu", PageWriter.Heading1);
            writer.Paragraph(Description(i));
            writer.Footer($"Trang {number}/{PageCount}", PageHeight);
        }

        // Page 1: one link of each kind. Rectangles are in PDF space (y up), under the description text.
        pages[0].AddDocumentLink(Rect(72, 600, 300, 620), 4);
        pages[0].AddDocumentLink(Rect(72, 570, 300, 590), NamedDestination);
        AddGoToLink(document, pages[0], Rect(72, 540, 300, 560), pages[2]);
        pages[0].AddWebLink(Rect(72, 510, 300, 530), "https://example.com/pdfpro");
        pages[2].AddDocumentLink(Rect(72, 600, 300, 620), 1);

        // Page 2: a sticky note and a highlight over the description.
        pages[1].Annotations.Add(new PdfTextAnnotation(document)
        {
            Title = "Người kiểm thử",
            Contents = "Ghi chú mẫu trên trang 2.",
            Icon = PdfTextAnnotationIcon.Comment,
            Rectangle = Rect(480, 740, 500, 760),
        });
        AddHighlight(document, pages[1], Rect(72, 730, 400, 748));

        AddNamedDestination(document, NamedDestination, pages[5]);
        AddBookmark(document, "Mục lục", pages[0]);
        AddBookmark(document, "Ghi chú và đánh dấu", pages[1]);
        AddBookmark(document, "Phụ lục", pages[5]);

        // Page labels: i for the cover, then 1, 2, … for the body.
        document.Internals.Catalog.Elements["/PageLabels"] = new PdfDictionary(document)
        {
            Elements =
            {
                ["/Nums"] = new PdfArray(
                    document,
                    new PdfInteger(0),
                    new PdfDictionary(document) { Elements = { ["/S"] = new PdfName("/r") } },
                    new PdfInteger(1),
                    new PdfDictionary(document) { Elements = { ["/S"] = new PdfName("/D") } }),
            },
        };
        document.Internals.Catalog.Elements["/OpenAction"] = new PdfArray(document, pages[0].Reference!, new PdfName("/Fit"));

        document.Save(path);
    }

    private static string Description(int index) => index switch
    {
        0 => "Mục lục: liên kết tới trang 4 (đích trực tiếp), tới phụ lục ở trang 6 (đích có tên), tới trang 3 (action GoTo) "
            + "và một liên kết web.",
        1 => "Trang này có một ghi chú dán và một vùng tô sáng để kiểm tra việc giữ chú thích khi sắp xếp hoặc nhân bản trang.",
        2 => "Trang 3 có liên kết quay lại trang 1.",
        5 => "Phụ lục là đích của một đích có tên (named destination).",
        _ => "Nội dung giả để kiểm tra thứ tự trang sau khi di chuyển, xóa hoặc chèn trang.",
    };

    private static PdfRectangle Rect(double left, double bottom, double right, double top)
        => new(new XPoint(left, bottom), new XPoint(right, top));

    private static void AddGoToLink(PdfDocument document, PdfPage page, PdfRectangle rect, PdfPage target)
    {
        var action = new PdfDictionary(document)
        {
            Elements =
            {
                ["/S"] = new PdfName("/GoTo"),
                ["/D"] = new PdfArray(document, target.Reference!, new PdfName("/Fit")),
            },
        };
        AddRawAnnotation(document, page, new PdfDictionary(document)
        {
            Elements =
            {
                ["/Type"] = new PdfName("/Annot"),
                ["/Subtype"] = new PdfName("/Link"),
                ["/Rect"] = rect,
                ["/Border"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)),
                ["/A"] = action,
            },
        });
    }

    private static void AddHighlight(PdfDocument document, PdfPage page, PdfRectangle rect)
    {
        AddRawAnnotation(document, page, new PdfDictionary(document)
        {
            Elements =
            {
                ["/Type"] = new PdfName("/Annot"),
                ["/Subtype"] = new PdfName("/Highlight"),
                ["/Rect"] = rect,
                ["/QuadPoints"] = new PdfArray(
                    document,
                    new PdfReal(rect.X1),
                    new PdfReal(rect.Y2),
                    new PdfReal(rect.X2),
                    new PdfReal(rect.Y2),
                    new PdfReal(rect.X1),
                    new PdfReal(rect.Y1),
                    new PdfReal(rect.X2),
                    new PdfReal(rect.Y1)),
                ["/C"] = new PdfArray(document, new PdfReal(1), new PdfReal(1), new PdfReal(0)),
                ["/T"] = new PdfString("Người kiểm thử", PdfStringEncoding.Unicode),
                ["/Contents"] = new PdfString("Tô sáng mẫu", PdfStringEncoding.Unicode),
            },
        });
    }

    private static void AddRawAnnotation(PdfDocument document, PdfPage page, PdfDictionary annotation)
    {
        document.Internals.AddObject(annotation);
        page.Annotations.Elements.Add(annotation.Reference!);
    }

    private static void AddNamedDestination(PdfDocument document, string name, PdfPage target)
    {
        var destination = new PdfArray(document, target.Reference!, new PdfName("/XYZ"), new PdfReal(0), new PdfReal(PageHeight), PdfNull.Value);
        document.Internals.Catalog.Elements["/Names"] = new PdfDictionary(document)
        {
            Elements =
            {
                ["/Dests"] = new PdfDictionary(document)
                {
                    Elements = { ["/Names"] = new PdfArray(document, new PdfString(name), destination) },
                },
            },
        };
    }

    private static void AddBookmark(PdfDocument document, string title, PdfPage page)
    {
        var outline = document.Outlines.Add(title, page, opened: true);
        outline.Top = PageHeight;
    }
}

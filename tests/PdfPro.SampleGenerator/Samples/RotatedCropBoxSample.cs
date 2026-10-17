using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// rotated-cropbox.pdf: one page per /Rotate value (0, 90, 180, 270) with crop/media boxes that do not start at (0,0).
/// Content is written as raw PDF operators in page space so tests know exactly where each marker is:
/// a 40 pt square in every corner of the visible box — red top-left, green top-right, blue bottom-left,
/// black bottom-right — on a light yellow background.
/// </summary>
internal sealed class RotatedCropBoxSample : ISample
{
    /// <summary>Page definitions shared with the tests: media box, optional crop box, /Rotate.</summary>
    public static readonly (PdfRectangleSpec MediaBox, PdfRectangleSpec? CropBox, int Rotate)[] Pages =
    [
        (new(0, 0, 595, 842), new(36, 72, 559, 770), 0),
        (new(0, 0, 595, 842), new(50, 100, 450, 700), 90),
        (new(0, 0, 595, 842), new(100, 50, 500, 650), 180),
        (new(100, 100, 695, 942), null, 270),
    ];

    public string FileName => "rotated-cropbox.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create("Trang xoay và CropBox lệch gốc", "Kiểm thử hệ tọa độ");
        foreach (var (mediaBox, cropBox, rotate) in Pages)
        {
            var page = document.AddPage();
            page.MediaBox = mediaBox.ToPdfRectangle();
            if (cropBox is { } crop)
            {
                page.CropBox = crop.ToPdfRectangle();
            }

            page.Rotate = rotate;
            var visible = cropBox ?? mediaBox;

            // An indirect content stream set directly on /Contents (PdfContents would drop a hand-written stream).
            var content = new PdfDictionary(document);
            document.Internals.AddObject(content);
            content.CreateStream(Encoding.ASCII.GetBytes(Markers(visible)));
            page.Elements.SetReference("/Contents", content);
        }

        document.Save(path);
    }

    private static string Markers(PdfRectangleSpec box)
    {
        const double size = 40;
        const double inset = 8;
        var sb = new StringBuilder();
        sb.AppendLine("q");
        Fill(sb, "1 1 0.85", box.Left, box.Bottom, box.Right - box.Left, box.Top - box.Bottom);
        Fill(sb, "1 0 0", box.Left + inset, box.Top - inset - size, size, size);
        Fill(sb, "0 0.6 0", box.Right - inset - size, box.Top - inset - size, size, size);
        Fill(sb, "0 0 1", box.Left + inset, box.Bottom + inset, size, size);
        Fill(sb, "0 0 0", box.Right - inset - size, box.Bottom + inset, size, size);
        sb.AppendLine("Q");
        return sb.ToString();
    }

    private static void Fill(StringBuilder sb, string rgb, double x, double y, double width, double height)
        => sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{rgb} rg {x} {y} {width} {height} re f"));
}

/// <summary>Rectangle in PDF user space (y up).</summary>
internal readonly record struct PdfRectangleSpec(double Left, double Bottom, double Right, double Top)
{
    public PdfRectangle ToPdfRectangle() => new(new XPoint(Left, Bottom), new XPoint(Right, Top));
}

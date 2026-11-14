using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.App.UiTests.TestSupport;

/// <summary>Documents the Phase 9 scenarios start from: a heavy scan, and a Word document.</summary>
internal static class ScanPdf
{
    private const int Width = 2480;
    private const int Height = 3508;

    private static readonly string[] Lines =
    [
        "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM",
        "Độc lập - Tự do - Hạnh phúc",
        "QUYẾT ĐỊNH",
        "Về việc thành lập Tổ kiểm thử phần mềm xử lý tài liệu",
        "Căn cứ Luật Tổ chức chính quyền địa phương ngày 19 tháng 6 năm 2015;",
        "Xét đề nghị của Văn phòng Ủy ban nhân dân xã,",
        "Điều 1. Thành lập Tổ kiểm thử gồm các ông (bà) có tên sau đây.",
        "Điều 2. Tổ kiểm thử có nhiệm vụ kiểm tra chất lượng bản quét.",
        "Điều 3. Quyết định này có hiệu lực kể từ ngày ký.",
    ];

    /// <summary>
    /// <paramref name="pages"/> A4 pages, each a 300 DPI colour JPEG (quality 95) of Vietnamese text on slightly
    /// noisy paper, as a scanner writes them — heavy on purpose.
    /// </summary>
    public static void Write(string path, int pages)
    {
        var jpegs = Sta(() => Enumerable.Range(1, pages).Select(Page).ToList());
        using var document = new PdfDocument();
        foreach (var jpeg in jpegs)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(Width * 72.0 / 300);
            page.Height = XUnit.FromPoint(Height * 72.0 / 300);
            using var graphics = XGraphics.FromPdfPage(page);
            using var image = XImage.FromStream(new MemoryStream(jpeg));
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }

        document.Save(path);
    }

    /// <summary>A Word document (.docx) of the paragraphs, written by hand (the smallest package Word and LibreOffice read).</summary>
    public static void WriteDocx(string path, params string[] paragraphs)
    {
        static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
        var body = string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t xml:space=\"preserve\">{Escape(p)}</w:t></w:r></w:p>"));
        (string Name, string Content)[] entries =
        [
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}</w:body></w:document>"),
        ];
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    /// <summary>The resolution (DPI) each image of the PDF is shown at, across the page width.</summary>
    public static List<double> ImageResolutions(string path)
    {
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var found = new List<double>();
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            foreach (var item in page.Resources.Elements.GetDictionary("/XObject")?.Elements.Values ?? [])
            {
                if ((item as PdfSharp.Pdf.Advanced.PdfReference)?.Value is PdfDictionary image && image.Elements.GetName("/Subtype") == "/Image")
                {
                    found.Add(image.Elements.GetInteger("/Width") / (page.Width.Point / 72));
                }
            }
        }

        return found;
    }

    private static byte[] Page(int number)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 243, 234)), null, new Rect(0, 0, Width, Height));
            var typeface = new Typeface("Times New Roman");
            var y = 260.0;
            for (var line = 0; y < Height - 300; line++, y += 78)
            {
                var text = line == 0 ? $"Trang {number}" : Lines[line % Lines.Length];
                context.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("vi-VN"), FlowDirection.LeftToRight, typeface, 50, Brushes.Black, 1.0), new Point(250, y));
            }
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        var random = new Random(number);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var noise = random.Next(-6, 7);
            for (var c = 0; c < 3; c++)
            {
                pixels[i + c] = (byte)Math.Clamp(pixels[i + c] + noise, 0, 255);
            }
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(Width, Height, 300, 300, PixelFormats.Bgr32, null, pixels, Width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>WPF draws on a single-threaded apartment.</summary>
    private static T Sta<T>(Func<T> work)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result! : throw new InvalidOperationException("The scan could not be drawn.", error);
    }
}

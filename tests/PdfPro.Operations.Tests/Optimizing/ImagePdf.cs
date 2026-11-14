using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfPro.Core.Geometry;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Optimizing;

/// <summary>
/// Builds PDF files with image XObjects drawn where a test wants them (at the dictionary level, so that the data is
/// exactly what the test gives), and reads images back out of files.
/// </summary>
internal sealed class ImagePdf : IDisposable
{
    private readonly PdfDocument _document = new();
    private readonly Dictionary<PdfPage, (StringBuilder Content, PdfDictionary XObjects)> _pages = [];
    private readonly Dictionary<PdfPage, PdfDictionary> _resources = [];
    private int _names;

    public PdfDocument Document => _document;

    public PdfPage Page(double width = 612, double height = 792)
    {
        var page = _document.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(width);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(height);
        _pages[page] = (new StringBuilder(), new PdfDictionary(_document));
        return page;
    }

    /// <summary>An 8-bit image of grey (1), RGB (3) or CMYK (4) samples, Flate-coded.</summary>
    public PdfDictionary Samples(byte[] samples, int width, int height, int channels, Action<PdfDictionary>? change = null)
    {
        var image = NewImage(width, height, Deflate(samples));
        image.Elements.SetName("/ColorSpace", channels switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", _ => "/DeviceCMYK" });
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.Elements.SetName("/Filter", "/FlateDecode");
        change?.Invoke(image);
        return image;
    }

    /// <summary>A JPEG image as it is.</summary>
    public PdfDictionary Jpeg(byte[] jpeg, int width, int height, int channels)
    {
        var image = NewImage(width, height, jpeg);
        image.Elements.SetName("/ColorSpace", channels == 1 ? "/DeviceGray" : "/DeviceRGB");
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.Elements.SetName("/Filter", "/DCTDecode");
        return image;
    }

    /// <summary>A 1-bit grey image (rows packed, 0 = black), Flate-coded.</summary>
    public PdfDictionary Bilevel(byte[] rows, int width, int height)
    {
        var image = NewImage(width, height, Deflate(rows));
        image.Elements.SetName("/ColorSpace", "/DeviceGray");
        image.Elements.SetInteger("/BitsPerComponent", 1);
        image.Elements.SetName("/Filter", "/FlateDecode");
        return image;
    }

    /// <summary>A form XObject of <paramref name="matrix"/> whose content draws <paramref name="image"/> with <paramref name="cm"/>.</summary>
    public PdfDictionary Form(PdfMatrix matrix, PdfDictionary image, PdfMatrix cm)
    {
        var xObjects = new PdfDictionary(_document);
        xObjects.Elements["/Im"] = image.Reference!;
        var resources = new PdfDictionary(_document);
        resources.Elements["/XObject"] = xObjects;
        var form = new PdfDictionary(_document);
        form.CreateStream(Encoding.ASCII.GetBytes($"q {Matrix(cm)} cm /Im Do Q"));
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = new PdfArray(_document, new PdfReal(-10000), new PdfReal(-10000), new PdfReal(10000), new PdfReal(10000));
        form.Elements["/Matrix"] = new PdfArray(_document, [.. new[] { matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F }.Select(v => new PdfReal(v))]);
        form.Elements["/Resources"] = resources;
        _document.Internals.AddObject(form);
        return form;
    }

    /// <summary>Paints <paramref name="xObject"/> on <paramref name="page"/> with <paramref name="cm"/>.</summary>
    public void Draw(PdfPage page, PdfDictionary xObject, PdfMatrix cm)
    {
        var (content, xObjects) = _pages[page];
        var name = "/X" + (++_names).ToString(CultureInfo.InvariantCulture);
        xObjects.Elements[name] = xObject.Reference!;
        content.Append(CultureInfo.InvariantCulture, $"q {Matrix(cm)} cm {name} Do Q\n");
    }

    /// <summary>The name <paramref name="xObject"/> has in the resources of <paramref name="page"/>.</summary>
    public string Register(PdfPage page, PdfDictionary xObject)
    {
        var name = "/X" + (++_names).ToString(CultureInfo.InvariantCulture);
        _pages[page].XObjects.Elements[name] = xObject.Reference!;
        return name;
    }

    /// <summary>Appends content operators to <paramref name="page"/> as they are.</summary>
    public void Raw(PdfPage page, string content) => _pages[page].Content.Append(content).Append('\n');

    /// <summary>The page's resource dictionary as it will be written (for fonts, patterns and graphics states).</summary>
    public PdfDictionary Resources(PdfPage page)
    {
        if (!_resources.TryGetValue(page, out var resources))
        {
            resources = new PdfDictionary(_document);
            _resources[page] = resources;
        }

        return resources;
    }

    /// <summary>Writes the file.</summary>
    public string Save(string path)
    {
        foreach (var (page, (content, xObjects)) in _pages)
        {
            var stream = new PdfDictionary(_document);
            stream.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
            _document.Internals.AddObject(stream);
            page.Elements["/Contents"] = stream.Reference!;
            var resources = Resources(page);
            resources.Elements["/XObject"] = xObjects;
            page.Elements["/Resources"] = resources;
        }

        _document.Save(path);
        return path;
    }

    public void Dispose() => _document.Dispose();

    /// <summary>The images drawn on the pages of a file (through forms too), each once, in the order found.</summary>
    public static List<PdfDictionary> Images(PdfDocument document)
    {
        var found = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in document.Pages)
        {
            Collect(AsDictionary(page.Elements["/Resources"]), 0);
        }

        return found;

        void Collect(PdfDictionary? resources, int depth)
        {
            if (depth > 8 || resources is null || AsDictionary(resources.Elements["/XObject"]) is not { } xObjects)
            {
                return;
            }

            foreach (var value in xObjects.Elements.Values)
            {
                if (AsDictionary(value) is not { } xObject || !seen.Add(xObject))
                {
                    continue;
                }

                if (Name(xObject, "/Subtype") == "/Image")
                {
                    found.Add(xObject);
                }
                else
                {
                    Collect(AsDictionary(xObject.Elements["/Resources"]), depth + 1);
                }
            }
        }
    }

    public static PdfDocument Read(string path) => PdfReader.Open(path, PdfDocumentOpenMode.Import);

    /// <summary>A photograph-like RGB picture: smooth colour fields with grain (Flate codes it poorly, JPEG well).</summary>
    public static byte[] Photo(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 3;
                var grain = random.Next(-6, 7);
                samples[at] = (byte)Math.Clamp((x * 255 / width) + grain, 0, 255);
                samples[at + 1] = (byte)Math.Clamp((y * 255 / height) + grain, 0, 255);
                samples[at + 2] = (byte)Math.Clamp((int)(128 + (100 * Math.Sin((x + y) / 40.0))) + grain, 0, 255);
            }
        }

        return samples;
    }

    /// <summary>
    /// A 1-bit page of "text": rows of small black blocks of random widths on white, then a few rows of noise (short runs
    /// of both colours) and a row whose only black pixel is far to the right (a white run longer than 2560).
    /// </summary>
    public static byte[] Scan(int width, int height, int seed = 3)
    {
        var random = new Random(seed);
        var stride = (width + 7) / 8;
        var rows = Enumerable.Repeat((byte)0xFF, stride * height).ToArray();
        void Black(int x, int y) => rows[(y * stride) + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
        for (var line = 10; line + 14 < height - 10; line += 24)
        {
            for (var x = 20; x < width - 40;)
            {
                var glyph = random.Next(3, 12);
                for (var gy = line; gy < line + random.Next(8, 14); gy++)
                {
                    for (var gx = x; gx < x + glyph; gx++)
                    {
                        Black(gx, gy);
                    }
                }

                x += glyph + random.Next(2, 9);
            }
        }

        for (var y = height - 10; y < height - 2; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (random.Next(3) == 0)
                {
                    Black(x, y);
                }
            }
        }

        Black(width - 3, height - 1);
        return rows;
    }

    private PdfDictionary NewImage(int width, int height, byte[] data)
    {
        var image = new PdfDictionary(_document);
        image.CreateStream(data);
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", width);
        image.Elements.SetInteger("/Height", height);
        _document.Internals.AddObject(image);
        return image;
    }

    private static string Matrix(PdfMatrix m) => string.Join(' ', new[] { m.A, m.B, m.C, m.D, m.E, m.F }.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}

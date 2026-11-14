using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfPro.Core.Geometry;

namespace PdfPro.Operations.Converting;

/// <summary>An image XObject: its dictionary entries in PDF syntax (without the brackets and /Length), its data, its soft mask.</summary>
internal sealed record PdfImageObject(string Entries, byte[] Data, PdfImageObject? SoftMask = null);

/// <summary>An image drawn on a page: its unit square mapped by <paramref name="Matrix"/> into the page.</summary>
internal sealed record PlacedImage(PdfImageObject Image, PdfMatrix Matrix);

/// <summary>
/// Writes a PDF of picture pages straight to a stream (PDF 1.7, classic cross-reference table): each image's data is
/// written as its page is added, so a document of hundreds of photographs never holds more than one in memory. Object
/// 1 is the page tree and 2 the catalog, written last with the cross-reference table.
/// </summary>
internal sealed class ImagePdfWriter
{
    private readonly Stream _output;
    private readonly long _start;
    private readonly Dictionary<int, long> _offsets = [];
    private readonly List<int> _pages = [];
    private int _next = 3;

    public ImagePdfWriter(Stream output)
    {
        _output = output;
        _start = output.Position;
        Write("%PDF-1.7\n");
        _output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']); // binary data follows
    }

    public int PageCount => _pages.Count;

    /// <summary>A page of <paramref name="size"/> points showing <paramref name="images"/> in order.</summary>
    public void AddPage(PdfSize size, IReadOnlyList<PlacedImage> images)
    {
        var resources = new StringBuilder();
        var content = new StringBuilder();
        for (var i = 0; i < images.Count; i++)
        {
            var number = WriteImage(images[i].Image);
            var m = images[i].Matrix;
            resources.Append(CultureInfo.InvariantCulture, $"/Im{i} {number} 0 R ");
            content.Append(CultureInfo.InvariantCulture, $"q {N(m.A)} {N(m.B)} {N(m.C)} {N(m.D)} {N(m.E)} {N(m.F)} cm /Im{i} Do Q\n");
        }

        var contents = WriteStream(string.Empty, Encoding.ASCII.GetBytes(content.ToString()));
        _pages.Add(WriteObject(
            $"<< /Type /Page /Parent 1 0 R /MediaBox [0 0 {N(size.Width)} {N(size.Height)}] /Resources << /XObject << {resources}>> >> /Contents {contents} 0 R >>"));
    }

    /// <summary>The page tree, the catalog, the cross-reference table and the trailer.</summary>
    public void Finish()
    {
        if (_pages.Count == 0)
        {
            throw new InvalidOperationException("A PDF needs at least one page.");
        }

        var kids = string.Join(' ', _pages.Select(p => p.ToString(CultureInfo.InvariantCulture) + " 0 R"));
        WriteObject(1, $"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>");
        WriteObject(2, "<< /Type /Catalog /Pages 1 0 R >>");
        var xref = _output.Position - _start;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {_next}\n0000000000 65535 f \n");
        for (var number = 1; number < _next; number++)
        {
            table.Append(CultureInfo.InvariantCulture, $"{_offsets[number]:D10} 00000 n \n");
        }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_next} /Root 2 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        Write(table.ToString());
        _output.Flush();
    }

    private static string N(double value) => Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);

    private int WriteImage(PdfImageObject image)
    {
        var mask = image.SoftMask is { } softMask ? $" /SMask {WriteImage(softMask)} 0 R" : string.Empty;
        return WriteStream(image.Entries + mask, image.Data);
    }

    private int WriteStream(string entries, byte[] data)
    {
        var number = _next++;
        _offsets[number] = _output.Position - _start;
        var separator = entries.Length > 0 ? " " : string.Empty;
        Write(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n<< {entries}{separator}/Length {data.Length} >>\nstream\n"));
        _output.Write(data);
        Write("\nendstream\nendobj\n");
        return number;
    }

    private int WriteObject(string body)
    {
        var number = _next++;
        WriteObject(number, body);
        return number;
    }

    private void WriteObject(int number, string body)
    {
        _offsets[number] = _output.Position - _start;
        Write(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n{body}\nendobj\n"));
    }

    private void Write(string text) => _output.Write(Encoding.ASCII.GetBytes(text));
}

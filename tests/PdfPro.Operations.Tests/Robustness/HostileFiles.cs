using System.IO.Compression;
using System.Text;

namespace PdfPro.Operations.Tests.Robustness;

/// <summary>PDF files built to break readers (SPEC Phase 13, "torture test"), written byte by byte with a correct cross-reference table.</summary>
internal static class HostileFiles
{
    private const string Content = "BT /F1 24 Tf 72 700 Td (Hello) Tj ET";
    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    /// <summary>Every kind, by name.</summary>
    public static IReadOnlyDictionary<string, Func<byte[]>> All { get; } = new Dictionary<string, Func<byte[]>>
    {
        ["zero-page"] = ZeroPage,
        ["cycle-pages"] = CyclePages,
        ["cycle-pages-2"] = CyclePagesTwo,
        ["cycle-outline"] = CycleOutline,
        ["deep-array"] = () => DeepArray(),
        ["bad-font"] = BadFont,
        ["ref-loop"] = ReferenceLoop,
        ["huge-page"] = HugePage,
        ["wrong-count"] = WrongCount,
        ["bomb-image"] = () => BombImage(30_000),
        ["truncated"] = Truncated,
    };

    /// <summary>Files PDFsharp would overflow its stack on.</summary>
    public static IReadOnlySet<string> StackBreakers { get; } = new HashSet<string> { "cycle-pages", "cycle-pages-2", "cycle-outline", "deep-array" };

    public static byte[] ZeroPage() => Pdf(Catalog(), Pages("3 0 R", 1), Page("[0 0 0 0]"), Stream(Content), Font);

    public static byte[] CyclePages() => Pdf(Catalog(), Pages("3 0 R 2 0 R", 2), Page("[0 0 612 792]"), Stream(Content), Font);

    public static byte[] CyclePagesTwo() => Pdf(
        Catalog(),
        Pages("6 0 R", 1),
        "<< /Type /Page /Parent 6 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        Stream(Content),
        Font,
        "<< /Type /Pages /Parent 2 0 R /Kids [2 0 R 3 0 R] /Count 1 >>");

    public static byte[] CycleOutline() => Pdf(
        "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
        Pages("3 0 R", 1),
        Page("[0 0 612 792]"),
        Stream(Content),
        Font,
        "<< /Type /Outlines /First 7 0 R /Last 8 0 R /Count 2 >>",
        "<< /Title (A) /Parent 6 0 R /Next 8 0 R /Dest [3 0 R /Fit] >>",
        "<< /Title (B) /Parent 6 0 R /Next 7 0 R /Dest [3 0 R /Fit] >>");

    public static byte[] DeepArray(int depth = 50_000) => Pdf(
        "<< /Type /Catalog /Pages 2 0 R /Junk " + new string('[', depth) + new string(']', depth) + " >>",
        Pages("3 0 R", 1),
        Page("[0 0 612 792]"),
        Stream(Content),
        Font);

    public static byte[] BadFont()
    {
        var garbage = new byte[5000];
        new Random(13).NextBytes(garbage);
        return Pdf(
            Catalog(),
            Pages("3 0 R", 1),
            Page("[0 0 612 792]"),
            Stream(Content),
            "<< /Type /Font /Subtype /TrueType /BaseFont /Broken /FirstChar 32 /LastChar 126 /Widths [] /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Broken /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 7 0 R >>",
            Stream(garbage, "/Length1 5000"));
    }

    public static byte[] ReferenceLoop() => Pdf(
        Catalog(),
        Pages("3 0 R", 1),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources 5 0 R >>",
        Stream("q /Fm1 Do Q"),
        "<< /XObject << /Fm1 6 0 R >> /Next 5 0 R >>",
        Stream(Encoding.ASCII.GetBytes("/Fm1 Do"), "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources 5 0 R"));

    public static byte[] HugePage() => Pdf(Catalog(), Pages("3 0 R", 1), Page("[0 0 14400 14400] /UserUnit 75000"), Stream(Content), Font);

    public static byte[] WrongCount() => Pdf(Catalog(), Pages("3 0 R", 1_000_000_000), Page("[0 0 612 792]"), Stream(Content), Font);

    /// <summary>A grey image of <paramref name="side"/>² pixels, all zero: a few hundred KB that decode to side² bytes.</summary>
    public static byte[] BombImage(int side) => Pdf(
        Catalog(),
        Pages("3 0 R", 1),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>",
        Stream("q 600 0 0 780 6 6 cm /Im1 Do Q"),
        Stream(Zeros((long)side * side), $"/Type /XObject /Subtype /Image /Width {side} /Height {side} /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode"));

    /// <summary>A page whose content decodes to <paramref name="megabytes"/> MB of spaces.</summary>
    public static byte[] BombContent(int megabytes) => Pdf(
        Catalog(),
        Pages("3 0 R", 1),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        Stream(Zeros((long)megabytes << 20, (byte)' '), "/Filter /FlateDecode"));

    public static byte[] Truncated()
    {
        var whole = Pdf(Catalog(), Pages("3 0 R", 1), Page("[0 0 612 792]"), Stream(Content), Font);
        return whole[..(whole.Length / 2)];
    }

    /// <summary>A document of blank pages.</summary>
    public static byte[] ManyPages(int count)
    {
        var builder = new StringBuilder();
        var kids = string.Join(' ', Enumerable.Range(0, count).Select(i => $"{i + 3} 0 R"));
        var objects = new List<string> { Catalog(), Pages(kids, count) };
        for (var i = 0; i < count; i++)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>");
        }

        return Pdf([.. objects]);
    }

    private static string Catalog() => "<< /Type /Catalog /Pages 2 0 R >>";

    private static string Pages(string kids, int count) => $"<< /Type /Pages /Kids [{kids}] /Count {count} >>";

    private static string Page(string mediaBox) => $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>";

    private static string Stream(string content) => Encoding.ASCII.GetString(StreamBytes(Encoding.ASCII.GetBytes(content), string.Empty));

    private static string Stream(byte[] data, string dictionary) => Encoding.Latin1.GetString(StreamBytes(data, dictionary));

    private static byte[] StreamBytes(byte[] data, string dictionary)
    {
        var head = Encoding.ASCII.GetBytes($"<< {dictionary} /Length {data.Length} >>\nstream\n");
        return [.. head, .. data, .. "\nendstream"u8];
    }

    private static byte[] Zeros(long count, byte value = 0)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[1 << 20];
            Array.Fill(chunk, value);
            for (long written = 0; written < count; written += chunk.Length)
            {
                zlib.Write(chunk, 0, (int)Math.Min(chunk.Length, count - written));
            }
        }

        return output.ToArray();
    }

    /// <summary>Objects numbered from 1, a cross-reference table with their offsets, a trailer.</summary>
    private static byte[] Pdf(params string[] objects)
    {
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        Write("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append($"{offset:D10} 00000 n \n");
        }

        Write(table.ToString());
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}

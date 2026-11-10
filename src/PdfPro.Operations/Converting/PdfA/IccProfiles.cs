using PdfSharp.Pdf;

namespace PdfPro.Operations.Converting.PdfA;

/// <summary>Colour profiles embedded into PDF/A documents.</summary>
internal static class IccProfiles
{
    /// <summary>The name PDFsharp 6.2.4 ships the ICC's sRGB profile under (sRGB2014.icc, free to copy and embed).</summary>
    private const string SrgbResource = "PdfSharp.Resources.sRGB2014.icc";

    private static readonly Lazy<byte[]> SrgbData = new(LoadSrgb, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<byte[]> CmykData = new(BuildCmyk, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A CMYK profile written by PDF_Pro (ICC version 2.1, input class, Lab connection space): a 9-point lookup table
    /// of the plain conversion viewers apply to DeviceCMYK — R = (1 − C)(1 − K)… in sRGB — so that CMYK colours keep the
    /// look they had while PDF/A finds them defined. It is not a press profile.
    /// </summary>
    public static byte[] Cmyk => CmykData.Value;

    /// <summary>
    /// The sRGB IEC61966-2.1 display profile of the International Color Consortium (sRGB2014.icc, ICC version 2, which
    /// PDF/A-1 needs), as PDFsharp carries it for its own PDF/A output.
    /// </summary>
    public static byte[] Srgb => SrgbData.Value;

    /// <summary>Whether <paramref name="profile"/> looks like an ICC profile of <paramref name="space"/> ("RGB ", "CMYK") of major version ≤ <paramref name="maxVersion"/>.</summary>
    public static bool IsProfile(ReadOnlySpan<byte> profile, string space, int maxVersion)
        => profile.Length >= 132
           && profile[36..40].SequenceEqual("acsp"u8)
           && profile[16..20].SequenceEqual(System.Text.Encoding.ASCII.GetBytes(space))
           && profile[8] is > 0 && profile[8] <= maxVersion;

    private static byte[] BuildCmyk()
    {
        const int Grid = 9;
        var tags = new (string Signature, byte[] Data)[]
        {
            ("desc", TextDescription("PDF_Pro CMYK (uncalibrated, as DeviceCMYK)")),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("A2B0", Lut(Grid)),
        };

        var offset = 128 + 4 + (tags.Length * 12);
        var placed = new List<(string Signature, int Offset, int Length)>();
        foreach (var (signature, data) in tags)
        {
            placed.Add((signature, offset, data.Length));
            offset += (data.Length + 3) & ~3;
        }

        var profile = new byte[offset];
        var span = profile.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(span, profile.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(span[8..], 0x02100000);
        Ascii(span[12..], "scnr");
        Ascii(span[16..], "CMYK");
        Ascii(span[20..], "Lab ");
        foreach (var (at, value) in new[] { (24, 2026), (26, 11), (28, 10) })
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(span[at..], (ushort)value);
        }

        Ascii(span[36..], "acsp");
        Xyz(0.9642, 1.0, 0.8249).AsSpan(8, 12).CopyTo(span[68..]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(span[128..], tags.Length);
        for (var i = 0; i < tags.Length; i++)
        {
            var entry = span[(132 + (i * 12))..];
            Ascii(entry, placed[i].Signature);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(entry[4..], placed[i].Offset);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(entry[8..], placed[i].Length);
            tags[i].Data.CopyTo(span[placed[i].Offset..]);
        }

        return profile;
    }

    /// <summary>lut8Type (mft1): 4 inputs, 3 outputs (Lab), identity matrix and curves, the grid of CMYK → Lab.</summary>
    private static byte[] Lut(int grid)
    {
        var points = grid * grid * grid * grid;
        var data = new byte[48 + (4 * 256) + (points * 3) + (3 * 256)];
        Ascii(data, "mft1");
        (data[8], data[9], data[10]) = (4, 3, (byte)grid);
        for (var i = 0; i < 3; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12 + (i * 16)), 0x00010000);
        }

        var at = 48;
        for (var channel = 0; channel < 4; channel++, at += 256)
        {
            for (var v = 0; v < 256; v++)
            {
                data[at + v] = (byte)v;
            }
        }

        for (var c = 0; c < grid; c++)
        {
            for (var m = 0; m < grid; m++)
            {
                for (var y = 0; y < grid; y++)
                {
                    for (var k = 0; k < grid; k++, at += 3)
                    {
                        var (l, a, b) = Lab(c / (grid - 1.0), m / (grid - 1.0), y / (grid - 1.0), k / (grid - 1.0));
                        data[at] = Byte(l * 255 / 100);
                        data[at + 1] = Byte(a + 128);
                        data[at + 2] = Byte(b + 128);
                    }
                }
            }
        }

        for (var channel = 0; channel < 3; channel++, at += 256)
        {
            for (var v = 0; v < 256; v++)
            {
                data[at + v] = (byte)v;
            }
        }

        return data;
    }

    /// <summary>CMYK as viewers show DeviceCMYK (sRGB = (1 − C)(1 − K)…), in CIE Lab under D50.</summary>
    internal static (double L, double A, double B) Lab(double c, double m, double y, double k)
    {
        static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        var (r, g, b) = (Linear((1 - c) * (1 - k)), Linear((1 - m) * (1 - k)), Linear((1 - y) * (1 - k)));
        var x = (0.4360747 * r) + (0.3850649 * g) + (0.1430804 * b);
        var yy = (0.2225045 * r) + (0.7168786 * g) + (0.0606169 * b);
        var z = (0.0139322 * r) + (0.0971045 * g) + (0.7141733 * b);
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116);
        var (fx, fy, fz) = (F(x / 0.9642), F(yy), F(z / 0.8249));
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static byte[] TextDescription(string text)
    {
        var data = new byte[12 + text.Length + 1 + 4 + 4 + 2 + 1 + 67];
        Ascii(data, "desc");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), text.Length + 1);
        Ascii(data.AsSpan(12), text);
        return data;
    }

    private static byte[] Text(string text)
    {
        var data = new byte[8 + text.Length + 1];
        Ascii(data, "text");
        Ascii(data.AsSpan(8), text);
        return data;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var data = new byte[20];
        Ascii(data, "XYZ ");
        foreach (var (at, value) in new[] { (8, x), (12, y), (16, z) })
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(at), (int)Math.Round(value * 65536));
        }

        return data;
    }

    private static void Ascii(Span<byte> target, string text) => System.Text.Encoding.ASCII.GetBytes(text, target);

    private static byte[] LoadSrgb()
    {
        using var stream = typeof(PdfDocument).Assembly.GetManifestResourceStream(SrgbResource)
            ?? throw new InvalidOperationException("PDFsharp no longer carries the sRGB profile.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var data = copy.ToArray();
        return IsProfile(data, "RGB ", 2) ? data : throw new InvalidOperationException("The sRGB profile PDFsharp carries is not an ICC version 2 RGB profile.");
    }
}

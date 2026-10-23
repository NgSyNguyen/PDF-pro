using System.Buffers.Binary;
using System.Text;
using PdfPro.Core.Text;

namespace PdfPro.Operations.Fonts;

/// <summary>
/// The parts of an OpenType/TrueType font file PDF_Pro needs: names, style, embedding permission, outline type,
/// vertical metrics, the character map and advance widths. Only the needed tables are read (the glyph outlines are
/// not), so scanning the installed fonts stays fast. Widths come straight from the 'hmtx' table, exactly what PDFsharp
/// embeds, so text measured here is laid out identically on screen and in the PDF.
/// </summary>
public sealed class TrueTypeFont : IFontMetrics
{
    // OS/2 fsType bits (OpenType spec): 0x0002 restricted license, 0x0004 preview & print, 0x0008 editable,
    // 0x0100 no subsetting, 0x0200 bitmap embedding only.
    private const ushort RestrictedLicense = 0x0002;
    private const ushort PreviewAndPrint = 0x0004;
    private const ushort Editable = 0x0008;
    private const ushort NoSubsetting = 0x0100;
    private const ushort BitmapOnly = 0x0200;

    private const int MaxTableLength = 64 * 1024 * 1024;

    private readonly Dictionary<int, ushort> _glyphs;
    private readonly ushort[] _advances;

    private TrueTypeFont(Dictionary<int, ushort> glyphs, ushort[] advances)
    {
        _glyphs = glyphs;
        _advances = advances;
    }

    /// <summary>Family name for the four-style model (name id 1), e.g. "Arial".</summary>
    public string FamilyName { get; private init; } = string.Empty;

    /// <summary>Style name (name id 2), e.g. "Bold Italic".</summary>
    public string SubfamilyName { get; private init; } = string.Empty;

    public bool IsBold { get; private init; }

    public bool IsItalic { get; private init; }

    /// <summary>True for TrueType outlines ('glyf'); false for PostScript (CFF) outlines, which PDFsharp cannot subset.</summary>
    public bool HasTrueTypeOutlines { get; private init; }

    /// <summary>True when the license lets the font be embedded as a subset in a document that can be edited later.</summary>
    public bool CanEmbedSubset { get; private init; }

    public int UnitsPerEm { get; private init; }

    public int Ascender { get; private init; }

    public int Descender { get; private init; }

    public int LineGap { get; private init; }

    /// <summary>Reads a single-font file (.ttf, .otf). Collections (.ttc) are refused: PDFsharp 6.2 cannot embed them.</summary>
    /// <exception cref="InvalidDataException">Not a font file this reader understands.</exception>
    public static TrueTypeFont Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        var header = Read(stream, 0, 12);
        var version = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (version == 0x74746366)
        {
            throw new InvalidDataException("Font collections are not supported.");
        }

        if (version is not (0x00010000 or 0x4F54544F or 0x74727565))
        {
            throw new InvalidDataException("Not an OpenType or TrueType font.");
        }

        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        var directory = Read(stream, 12, tableCount * 16);
        var tables = new Dictionary<string, (uint Offset, uint Length)>(StringComparer.Ordinal);
        for (var i = 0; i < tableCount; i++)
        {
            var record = directory.AsSpan(i * 16, 16);
            tables[Encoding.ASCII.GetString(record[..4])] = (BinaryPrimitives.ReadUInt32BigEndian(record[8..]), BinaryPrimitives.ReadUInt32BigEndian(record[12..]));
        }

        byte[] Table(string tag) => tables.TryGetValue(tag, out var entry)
            ? Read(stream, entry.Offset, entry.Length)
            : throw new InvalidDataException($"The font has no '{tag}' table.");

        var head = Table("head");
        var hhea = Table("hhea");
        var maxp = Table("maxp");
        var unitsPerEm = U16(head, 18);
        if (unitsPerEm is < 16 or > 16384)
        {
            throw new InvalidDataException("The font's units per em are out of range.");
        }

        var glyphCount = U16(maxp, 4);
        var metricCount = U16(hhea, 34);
        if (metricCount == 0 || metricCount > glyphCount)
        {
            throw new InvalidDataException("The font's horizontal metrics are inconsistent.");
        }

        var hmtx = Table("hmtx");
        var advances = new ushort[metricCount];
        for (var i = 0; i < metricCount; i++)
        {
            advances[i] = U16(hmtx, i * 4);
        }

        var macStyle = U16(head, 44);
        var bold = (macStyle & 1) != 0;
        var italic = (macStyle & 2) != 0;
        ushort fsType = 0;
        if (tables.ContainsKey("OS/2"))
        {
            var os2 = Table("OS/2");
            fsType = U16(os2, 8);
            var selection = U16(os2, 62);
            italic = (selection & 0x01) != 0;
            bold = (selection & 0x20) != 0;
        }

        var names = tables.ContainsKey("name") ? ReadNames(Table("name")) : [];
        return new TrueTypeFont(ReadCharacterMap(Table("cmap")), advances)
        {
            FamilyName = names.GetValueOrDefault(1, string.Empty),
            SubfamilyName = names.GetValueOrDefault(2, string.Empty),
            IsBold = bold,
            IsItalic = italic,
            HasTrueTypeOutlines = tables.ContainsKey("glyf"),
            CanEmbedSubset = AllowsSubsetEmbedding(fsType),
            UnitsPerEm = unitsPerEm,
            Ascender = I16(hhea, 4),
            Descender = I16(hhea, 6),
            LineGap = I16(hhea, 8),
        };
    }

    public static TrueTypeFont Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream(data, writable: false);
        return Load(stream);
    }

    public bool HasGlyph(int codePoint) => _glyphs.ContainsKey(codePoint);

    /// <summary>True when every letter of <paramref name="text"/> (spaces aside) has a glyph.</summary>
    public bool Covers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && !HasGlyph(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    public int Advance(int codePoint)
    {
        var glyph = _glyphs.TryGetValue(codePoint, out var found) ? found : 0;
        return _advances[Math.Min(glyph, _advances.Length - 1)];
    }

    internal static bool AllowsSubsetEmbedding(ushort fsType)
    {
        if ((fsType & (NoSubsetting | BitmapOnly)) != 0)
        {
            return false;
        }

        // Restricted-license fonts may not be embedded at all; preview & print fonts only in read-only documents.
        return (fsType & (RestrictedLicense | PreviewAndPrint)) == 0 || (fsType & Editable) != 0;
    }

    private static Dictionary<int, string> ReadNames(byte[] name)
    {
        var names = new Dictionary<int, string>();
        var ranks = new Dictionary<int, int>();
        var count = U16(name, 2);
        var storage = U16(name, 4);
        for (var i = 0; i < count; i++)
        {
            var record = 6 + (i * 12);
            var (platform, encoding, language, id) = (U16(name, record), U16(name, record + 2), U16(name, record + 4), U16(name, record + 6));
            var (length, offset) = (U16(name, record + 8), U16(name, record + 10));
            if (id is not (1 or 2) || storage + offset + length > name.Length)
            {
                continue;
            }

            // Prefer Windows Unicode US English, then any Windows Unicode, then Mac Roman.
            var rank = (platform, encoding) switch
            {
                (3, 1 or 10) when language == 0x409 => 3,
                (3, 1 or 10) => 2,
                (0, _) => 2,
                (1, 0) => 1,
                _ => 0,
            };
            if (rank == 0 || ranks.GetValueOrDefault(id) >= rank)
            {
                continue;
            }

            var bytes = name.AsSpan(storage + offset, length);
            names[id] = rank == 1 ? Encoding.Latin1.GetString(bytes) : Encoding.BigEndianUnicode.GetString(bytes);
            ranks[id] = rank;
        }

        return names;
    }

    private static Dictionary<int, ushort> ReadCharacterMap(byte[] cmap)
    {
        var count = U16(cmap, 2);
        var best = -1;
        var bestRank = 0;
        for (var i = 0; i < count; i++)
        {
            var record = 4 + (i * 8);
            var (platform, encoding) = (U16(cmap, record), U16(cmap, record + 2));
            var offset = checked((int)U32(cmap, record + 4));
            var format = U16(cmap, offset);
            var rank = (platform, encoding, format) switch
            {
                (3, 10, 12) => 4,
                (0, _, 12) => 3,
                (3, 1, 4) => 2,
                (0, _, 4) => 1,
                _ => 0,
            };
            if (rank > bestRank)
            {
                (best, bestRank) = (offset, rank);
            }
        }

        if (best < 0)
        {
            throw new InvalidDataException("The font has no Unicode character map.");
        }

        return U16(cmap, best) == 12 ? ReadFormat12(cmap, best) : ReadFormat4(cmap, best);
    }

    private static Dictionary<int, ushort> ReadFormat4(byte[] cmap, int table)
    {
        var map = new Dictionary<int, ushort>();
        var segments = U16(cmap, table + 6) / 2;
        var ends = table + 14;
        var starts = ends + (segments * 2) + 2;
        var deltas = starts + (segments * 2);
        var rangeOffsets = deltas + (segments * 2);
        for (var s = 0; s < segments; s++)
        {
            var (start, end) = (U16(cmap, starts + (s * 2)), U16(cmap, ends + (s * 2)));
            var delta = I16(cmap, deltas + (s * 2));
            var rangeOffset = U16(cmap, rangeOffsets + (s * 2));
            for (int c = start; c <= end && c < 0xFFFF; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    var at = rangeOffsets + (s * 2) + rangeOffset + ((c - start) * 2);
                    glyph = U16(cmap, at);
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0)
                {
                    map[c] = (ushort)glyph;
                }
            }
        }

        return map;
    }

    private static Dictionary<int, ushort> ReadFormat12(byte[] cmap, int table)
    {
        var map = new Dictionary<int, ushort>();
        var groups = U32(cmap, table + 12);
        for (var g = 0u; g < groups; g++)
        {
            var record = table + 16 + checked((int)(g * 12));
            var (start, end, glyph) = (U32(cmap, record), U32(cmap, record + 4), U32(cmap, record + 8));
            if (end < start || end > 0x10FFFF || end - start > 0x10000)
            {
                throw new InvalidDataException("The font's character map is damaged.");
            }

            for (var c = start; c <= end; c++)
            {
                var id = glyph + (c - start);
                if (id is > 0 and <= ushort.MaxValue)
                {
                    map[(int)c] = (ushort)id;
                }
            }
        }

        return map;
    }

    private static byte[] Read(Stream stream, long offset, long length)
    {
        if (length is < 0 or > MaxTableLength || offset < 0 || offset + length > stream.Length)
        {
            throw new InvalidDataException("The font file is truncated or damaged.");
        }

        var buffer = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static ushort U16(byte[] data, int offset)
        => offset >= 0 && offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)) : throw Damaged();

    private static short I16(byte[] data, int offset)
        => offset >= 0 && offset + 2 <= data.Length ? BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset)) : throw Damaged();

    private static uint U32(byte[] data, int offset)
        => offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)) : throw Damaged();

    private static InvalidDataException Damaged() => new("The font file is truncated or damaged.");
}

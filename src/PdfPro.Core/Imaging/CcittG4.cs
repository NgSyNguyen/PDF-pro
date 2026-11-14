namespace PdfPro.Core.Imaging;

/// <summary>
/// CCITT Group 4 (ITU-T T.6) encoding of 1-bit images: the compression of fax machines and document scanners, several
/// times smaller than Flate on scanned text. Every row is coded against the one above it (the first against a white
/// row), the data ends with EOFB and no row is byte-aligned — in PDF terms <c>/CCITTFaxDecode</c> with <c>/K -1</c>,
/// <c>/Columns</c> = width, <c>/Rows</c> = height and the default <c>/BlackIs1 false</c>, <c>/EncodedByteAlign false</c>,
/// <c>/EndOfBlock true</c>.
/// </summary>
/// <remarks>
/// Rows are packed most significant bit first, each starting on a byte; a 0 bit is coded as black and a 1 bit as white,
/// which is what a decoder gives back with <c>/BlackIs1 false</c>: the bits come out exactly as they went in.
/// </remarks>
public static class CcittG4
{
    /// <summary>Codes of white runs 0–63, then of 64, 128… 1728 (T.4 tables 2 and 3).</summary>
    private static readonly string[] WhiteCodes =
    [
        "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111",
        "10011", "10100", "00111", "01000", "001000", "000011", "110100", "110101",
        "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100",
        "0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010",
        "00011011", "00010010", "00010011", "00010100", "00010101", "00010110", "00010111", "00101000",
        "00101001", "00101010", "00101011", "00101100", "00101101", "00000100", "00000101", "00001010",
        "00001011", "01010010", "01010011", "01010100", "01010101", "00100100", "00100101", "01011000",
        "01011001", "01011010", "01011011", "01001010", "01001011", "00110010", "00110011", "00110100",
        "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101",
        "01101000", "01100111", "011001100", "011001101", "011010010", "011010011", "011010100", "011010101",
        "011010110", "011010111", "011011000", "011011001", "011011010", "011011011", "010011000", "010011001",
        "010011010", "011000", "010011011",
    ];

    /// <summary>Codes of black runs 0–63, then of 64, 128… 1728.</summary>
    private static readonly string[] BlackCodes =
    [
        "0000110111", "010", "11", "10", "011", "0011", "0010", "00011",
        "000101", "000100", "0000100", "0000101", "0000111", "00000100", "00000111", "000011000",
        "0000010111", "0000011000", "0000001000", "00001100111", "00001101000", "00001101100", "00000110111", "00000101000",
        "00000010111", "00000011000", "000011001010", "000011001011", "000011001100", "000011001101", "000001101000", "000001101001",
        "000001101010", "000001101011", "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111",
        "000001101100", "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
        "000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111",
        "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110", "000001100111",
        "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101", "0000001101100",
        "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010", "0000001110011", "0000001110100",
        "0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010",
        "0000001011011", "0000001100100", "0000001100101",
    ];

    /// <summary>Codes of 1792, 1856… 2560, the same for both colours (T.4 table 4).</summary>
    private static readonly string[] ExtendedCodes =
    [
        "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101",
        "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
    ];

    private static readonly (int Code, int Length) Pass = Parse("0001");
    private static readonly (int Code, int Length) Horizontal = Parse("001");
    private static readonly (int Code, int Length) EndOfLine = Parse("000000000001");

    /// <summary>Vertical mode codes for a1 − b1 = −3 … 3.</summary>
    private static readonly (int Code, int Length)[] Vertical = [.. new[] { "0000010", "000010", "010", "1", "011", "000011", "0000011" }.Select(Parse)];

    private static readonly (int Code, int Length)[] White = [.. WhiteCodes.Select(Parse)];
    private static readonly (int Code, int Length)[] Black = [.. BlackCodes.Select(Parse)];
    private static readonly (int Code, int Length)[] Extended = [.. ExtendedCodes.Select(Parse)];

    /// <summary>The code tables as bit strings, for tests: white, black, then the extended make-up codes.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Tables => [WhiteCodes, BlackCodes, ExtendedCodes];

    /// <summary>The G4 data of a 1-bit image of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    /// <param name="rows">The rows, packed (most significant bit first), each (width + 7) / 8 bytes; 0 = black.</param>
    public static byte[] Encode(ReadOnlySpan<byte> rows, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var stride = (width + 7) / 8;
        if (rows.Length < (long)stride * height)
        {
            throw new ArgumentException("The rows are shorter than the image.", nameof(rows));
        }

        var writer = new BitWriter(Math.Max(64, rows.Length / 8));

        // Changing elements: positions where a pixel differs from the one before it (the pixel before the row is white),
        // then "width" twice as sentinels.
        var reference = new int[width + 3];
        var coding = new int[width + 3];
        reference[0] = width;
        reference[1] = width;
        var referenceCount = 0;
        for (var y = 0; y < height; y++)
        {
            var codingCount = Changes(rows.Slice(y * stride, stride), width, coding);
            EncodeRow(writer, width, reference, referenceCount, coding);
            (reference, coding) = (coding, reference);
            referenceCount = codingCount;
        }

        // End of facsimile block: two EOLs.
        writer.Write(EndOfLine);
        writer.Write(EndOfLine);
        return writer.ToArray();
    }

    /// <summary>The changing elements of a row (colour alternates from white at each), with two sentinels; their count.</summary>
    private static int Changes(ReadOnlySpan<byte> row, int width, int[] changes)
    {
        var count = 0;
        var black = false;
        for (var x = 0; x < width; x++)
        {
            var isBlack = (row[x >> 3] & (0x80 >> (x & 7))) == 0;
            if (isBlack != black)
            {
                changes[count++] = x;
                black = isBlack;
            }
        }

        changes[count] = width;
        changes[count + 1] = width;
        return count;
    }

    private static void EncodeRow(BitWriter writer, int width, int[] reference, int referenceCount, int[] coding)
    {
        var a0 = -1;
        var black = false;
        var codingIndex = 0; // the first changing element of the coding row after a0
        var referenceIndex = 0; // the first changing element of the reference row after a0
        while (a0 < width)
        {
            // a1: the next changing element on the coding row after a0 (the elements alternate colour, so it has the
            // colour opposite to a0's).
            while (coding[codingIndex] <= a0 && coding[codingIndex] < width)
            {
                codingIndex++;
            }

            var a1 = coding[codingIndex];
            var a2 = coding[codingIndex + 1];

            // b1: the first changing element on the reference row after a0 of the colour opposite to a0's (element i
            // turns the row black when i is even); b2 the one after it. The sentinels stand at "width".
            while (reference[referenceIndex] <= a0 && reference[referenceIndex] < width)
            {
                referenceIndex++;
            }

            var b1Index = referenceIndex < referenceCount && ((referenceIndex & 1) == 0) == black ? referenceIndex + 1 : referenceIndex;
            var b1 = b1Index < referenceCount ? reference[b1Index] : width;
            var b2 = b1Index + 1 < referenceCount ? reference[b1Index + 1] : width;

            if (b2 < a1)
            {
                writer.Write(Pass);
                a0 = b2;
                continue;
            }

            var offset = a1 - b1;
            if (offset is >= -3 and <= 3)
            {
                writer.Write(Vertical[offset + 3]);
                a0 = a1;
                black = !black;
                continue;
            }

            writer.Write(Horizontal);
            var start = Math.Max(a0, 0);
            WriteRun(writer, a1 - start, black);
            WriteRun(writer, a2 - a1, !black);
            a0 = a2;
        }
    }

    private static void WriteRun(BitWriter writer, int length, bool black)
    {
        var codes = black ? Black : White;
        while (length > 2560)
        {
            writer.Write(Extended[^1]);
            length -= 2560;
        }

        if (length >= 1792)
        {
            writer.Write(Extended[(length / 64) - 28]);
            length %= 64;
        }
        else if (length >= 64)
        {
            writer.Write(codes[63 + (length / 64)]);
            length %= 64;
        }

        writer.Write(codes[length]);
    }

    private static (int Code, int Length) Parse(string bits) => (Convert.ToInt32(bits, 2), bits.Length);

    /// <summary>Bits written most significant first.</summary>
    private sealed class BitWriter(int capacity)
    {
        private readonly MemoryStream _output = new(capacity);
        private int _buffer;
        private int _count;

        public void Write((int Code, int Length) code)
        {
            for (var i = code.Length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((code.Code >> i) & 1);
                if (++_count == 8)
                {
                    _output.WriteByte((byte)_buffer);
                    _buffer = 0;
                    _count = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            if (_count > 0)
            {
                _output.WriteByte((byte)(_buffer << (8 - _count)));
                _buffer = 0;
                _count = 0;
            }

            return _output.ToArray();
        }
    }
}

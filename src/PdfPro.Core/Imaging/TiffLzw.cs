namespace PdfPro.Core.Imaging;

/// <summary>
/// LZW as TIFF 6.0 (section 13) uses it: codes of 9 to 12 bits written most significant bit first, Clear (256) first
/// and whenever the table fills, EndOfInformation (257) last, and the code width growing one code early (when the
/// next code would be 511, 1023 or 2047) — the "early change" every TIFF reader expects.
/// </summary>
public static class TiffLzw
{
    private const int Clear = 256;
    private const int EndOfInformation = 257;
    private const int FirstCode = 258;
    private const int MaxCode = 4094;
    private const int Keys = 4096 * 256;

    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var output = new BitWriter(Math.Max(64, data.Length / 2));
        var codes = new int[Keys];
        var stamps = new int[Keys];
        var generation = 1;
        var width = 9;
        var next = FirstCode;
        output.Write(Clear, width);
        if (data.IsEmpty)
        {
            output.Write(EndOfInformation, width);
            return output.ToArray();
        }

        var current = (int)data[0];
        for (var i = 1; i < data.Length; i++)
        {
            var b = data[i];
            var key = (current << 8) | b;
            if (stamps[key] == generation)
            {
                current = codes[key];
                continue;
            }

            output.Write(current, width);
            codes[key] = next;
            stamps[key] = generation;
            next++;
            if (next >= MaxCode)
            {
                output.Write(Clear, width);
                generation++;
                width = 9;
                next = FirstCode;
            }
            else if (next > (1 << width) - 1 && width < 12)
            {
                // The decoder adds its entries one code later: it widens when its next code would be 2^n − 1.
                width++;
            }

            current = b;
        }

        output.Write(current, width);
        next++;
        if (next >= MaxCode)
        {
            output.Write(Clear, width);
            width = 9;
        }
        else if (next > (1 << width) - 1 && width < 12)
        {
            width++;
        }

        output.Write(EndOfInformation, width);
        return output.ToArray();
    }

    /// <summary>The data LZW-coded <paramref name="encoded"/> stands for (stops at EndOfInformation or the end).</summary>
    /// <exception cref="InvalidDataException">A code that is not in the table.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> encoded)
    {
        var output = new List<byte>(encoded.Length * 3);
        var prefixes = new int[4096];
        var suffixes = new byte[4096];
        var lengths = new int[4096];
        for (var i = 0; i < 256; i++)
        {
            suffixes[i] = (byte)i;
            lengths[i] = 1;
        }

        var reader = new BitReader(encoded);
        var width = 9;
        var next = FirstCode;
        var previous = -1;
        var scratch = new byte[4096];
        while (reader.TryRead(width, out var code))
        {
            if (code == EndOfInformation)
            {
                break;
            }

            if (code == Clear)
            {
                width = 9;
                next = FirstCode;
                previous = -1;
                continue;
            }

            int first;
            if (code < next && (code < 256 || code >= FirstCode))
            {
                first = Emit(code);
                if (previous >= 0)
                {
                    Add(previous, (byte)first);
                }
            }
            else if (code == next && previous >= 0)
            {
                var start = output.Count;
                Emit(previous);
                var head = output[start];
                output.Add(head);
                Add(previous, head);
            }
            else
            {
                throw new InvalidDataException("An LZW code is not in the table.");
            }

            previous = code;
            if (next == (1 << width) - 1 && width < 12)
            {
                width++;
            }
        }

        return [.. output];

        int Emit(int code)
        {
            var length = lengths[code];
            for (var at = length - 1; at >= 0; at--)
            {
                scratch[at] = suffixes[code];
                code = prefixes[code];
            }

            for (var i = 0; i < length; i++)
            {
                output.Add(scratch[i]);
            }

            return scratch[0];
        }

        void Add(int prefix, byte suffix)
        {
            if (next >= 4096)
            {
                return;
            }

            prefixes[next] = prefix;
            suffixes[next] = suffix;
            lengths[next] = lengths[prefix] + 1;
            next++;
        }
    }

    private sealed class BitWriter(int capacity)
    {
        private readonly List<byte> _bytes = new(capacity);
        private int _buffer;
        private int _bits;

        public void Write(int code, int width)
        {
            _buffer = (_buffer << width) | code;
            _bits += width;
            while (_bits >= 8)
            {
                _bytes.Add((byte)(_buffer >> (_bits - 8)));
                _bits -= 8;
            }

            _buffer &= (1 << _bits) - 1;
        }

        public byte[] ToArray()
        {
            if (_bits > 0)
            {
                _bytes.Add((byte)(_buffer << (8 - _bits)));
                _bits = 0;
                _buffer = 0;
            }

            return [.. _bytes];
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _bit;

        public bool TryRead(int width, out int code)
        {
            code = 0;
            if (_bit + width > (long)_data.Length * 8)
            {
                return false;
            }

            for (var i = 0; i < width; i++, _bit++)
            {
                code = (code << 1) | ((_data[(int)(_bit >> 3)] >> (7 - (int)(_bit & 7))) & 1);
            }

            return true;
        }
    }
}

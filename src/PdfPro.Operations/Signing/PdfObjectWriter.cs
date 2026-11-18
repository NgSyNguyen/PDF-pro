using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfPro.Operations.Signing;

/// <summary>
/// Writes PDFsharp objects in PDF syntax for an incremental update (PDFsharp itself only writes whole files).
/// References are written through <see cref="Reference"/>, so objects read from a file keep their numbers and objects
/// from another document can be given new ones. Strings are written as hex: nothing to escape.
/// </summary>
internal sealed class PdfObjectWriter(Func<PdfReference, string> reference)
{
    /// <summary>"n g R" for a reference.</summary>
    public Func<PdfReference, string> Reference { get; } = reference;

    /// <summary>The body of an indirect object: its value, and for a stream its data with an exact /Length.</summary>
    public byte[] Body(PdfObject value)
    {
        using var output = new MemoryStream();
        if (value is PdfDictionary { Stream: { } stream } dictionary)
        {
            var data = stream.Value;
            var text = new StringBuilder("<<");
            foreach (var (key, item) in dictionary.Elements.Where(e => e.Key != "/Length"))
            {
                text.Append(Name(key)).Append(' ');
                Write(text, item);
            }

            text.Append(CultureInfo.InvariantCulture, $"/Length {data.Length}>>\nstream\n");
            output.Write(Encoding.Latin1.GetBytes(text.ToString()));
            output.Write(data);
            output.Write("\nendstream"u8);
        }
        else
        {
            var text = new StringBuilder();
            Write(text, value, direct: true);
            output.Write(Encoding.Latin1.GetBytes(text.ToString()));
        }

        return output.ToArray();
    }

    /// <summary>Writes a value; an indirect object met inside another is written as a reference.</summary>
    public void Write(StringBuilder text, PdfItem? item, bool direct = false)
    {
        if (!direct && item is PdfObject { Reference: { } indirect })
        {
            text.Append(Reference(indirect));
            return;
        }

        switch (item)
        {
            case null or PdfNull or PdfNullObject:
                text.Append("null");
                break;
            case PdfReference reference:
                text.Append(Reference(reference));
                break;
            case PdfBoolean boolean:
                text.Append(boolean.Value ? "true" : "false");
                break;
            case PdfBooleanObject boolean:
                text.Append(boolean.Value ? "true" : "false");
                break;
            case PdfInteger integer:
                text.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfIntegerObject integer:
                text.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfLongInteger integer:
                text.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfLongIntegerObject integer:
                text.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfReal real:
                text.Append(Number(real.Value));
                break;
            case PdfRealObject real:
                text.Append(Number(real.Value));
                break;
            case PdfName name:
                text.Append(Name(name.Value));
                break;
            case PdfNameObject name:
                text.Append(Name(name.Value));
                break;
            case PdfString value:
                text.Append('<').Append(Convert.ToHexString(Bytes(value))).Append('>');
                break;
            case PdfStringObject value:
                text.Append('<').Append(Convert.ToHexString(Encoding.Latin1.GetBytes(value.Value))).Append('>');
                break;
            case PdfDate date:
                text.Append('(').Append(Core.Documents.PdfDate.Format(date.Value)).Append(')');
                break;
            case PdfLiteral literal:
                text.Append(literal.Value);
                break;
            case PdfRectangle rectangle:
                text.Append('[').Append(Number(rectangle.X1)).Append(' ').Append(Number(rectangle.Y1)).Append(' ')
                    .Append(Number(rectangle.X2)).Append(' ').Append(Number(rectangle.Y2)).Append(']');
                break;
            case PdfArray array:
                text.Append('[');
                foreach (var element in array.Elements)
                {
                    Write(text, element);
                    text.Append(' ');
                }

                text.Append(']');
                break;
            case PdfDictionary dictionary when dictionary.Stream is null:
                text.Append("<<");
                foreach (var (key, value) in dictionary.Elements)
                {
                    text.Append(Name(key)).Append(' ');
                    Write(text, value);
                }

                text.Append(">>");
                break;
            default:
                throw new NotSupportedException($"A {item.GetType().Name} cannot be written into an incremental update.");
        }
    }

    /// <summary>The bytes a string was read from: UTF-16 with its byte order mark, or one byte per character.</summary>
    private static byte[] Bytes(PdfString value)
        => value.Encoding == PdfStringEncoding.Unicode
            ? [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(value.Value)]
            : [.. value.Value.Select(c => (byte)Math.Min((int)c, 0xFF))];

    /// <summary>A name with the characters that cannot appear in it written as #xx.</summary>
    public static string Name(string value)
    {
        var name = value.StartsWith('/') ? value[1..] : value;
        var text = new StringBuilder("/");
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            if (b is < 0x21 or > 0x7E or (byte)'#' or (byte)'/' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'%')
            {
                text.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append((char)b);
            }
        }

        return text.ToString();
    }

    /// <summary>A text string: ASCII as a literal, anything else as UTF-16 with its byte order mark (7.9.2.2).</summary>
    public static string Text(string value)
    {
        if (value.All(c => c is >= ' ' and <= '~'))
        {
            return "(" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal) + ")";
        }

        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";
    }

    public static string Number(double value)
        => Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);
}

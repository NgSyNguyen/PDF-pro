using System.Text;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Forms;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Signing;

/// <summary>A signed signature field: its name, where it shows and what its signature dictionary says.</summary>
internal sealed record SignatureFieldInfo(string Name, IReadOnlyList<long> ByteRange, int Page, PdfRect Box, string? Location, string? ContactInfo);

/// <summary>The signed signature fields of a file, read with PDFsharp (PDFium does not tell a signature's field).</summary>
internal static class SignatureFields
{
    /// <summary>The signed fields; none when the file cannot be read this way.</summary>
    public static IReadOnlyList<SignatureFieldInfo> Read(byte[] file)
    {
        try
        {
            using var document = PdfReader.Open(new MemoryStream(file, writable: false), PdfDocumentOpenMode.Import);
            var result = new List<SignatureFieldInfo>();
            foreach (var (field, dictionary, _) in FormReader.Fields(document))
            {
                if (!field.IsSigned || Resolve(dictionary.Elements["/V"]) is not PdfDictionary signature
                    || Resolve(signature.Elements["/ByteRange"]) is not PdfArray range)
                {
                    continue;
                }

                var widget = field.Widgets.FirstOrDefault();
                result.Add(new SignatureFieldInfo(
                    field.Name,
                    [.. range.Elements.Select(Number)],
                    widget is { Rect.IsEmpty: false } ? widget.Page : -1,
                    widget?.Rect ?? default,
                    Text(signature.Elements["/Location"]),
                    Text(signature.Elements["/ContactInfo"])));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    private static long Number(PdfItem item) => Resolve(item) switch
    {
        PdfInteger integer => integer.Value,
        PdfLongInteger integer => integer.Value,
        PdfReal real => (long)real.Value,
        _ => -1,
    };

    private static string? Text(PdfItem? item) => Resolve(item) is PdfString { Value.Length: > 0 } text ? text.Value : null;

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
}

/// <summary>The revisions of a file: each incremental update ends with its own "%%EOF".</summary>
internal static class Revisions
{
    private static readonly byte[] EndMarker = Encoding.ASCII.GetBytes("%%EOF");

    /// <summary>How many revisions end after <paramref name="offset"/>.</summary>
    public static int CountAfter(byte[] file, long offset)
    {
        var count = 0;
        var rest = file.AsSpan((int)Math.Clamp(offset, 0, file.Length));
        int at;
        while ((at = rest.IndexOf(EndMarker)) >= 0)
        {
            count++;
            rest = rest[(at + EndMarker.Length)..];
        }

        return count;
    }
}

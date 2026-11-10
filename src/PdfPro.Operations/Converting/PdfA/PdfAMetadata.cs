using System.Globalization;
using System.Security;
using System.Text;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Converting.PdfA;

/// <summary>
/// The document information and the XMP packet of a PDF/A document, written from the same values so that they agree
/// (ISO 19005-1 6.7.3, 19005-2 6.6.2.3): title ↔ dc:title, author ↔ dc:creator, subject ↔ dc:description, keywords ↔
/// pdf:Keywords, creator ↔ xmp:CreatorTool, creation and modification dates ↔ xmp:CreateDate and xmp:ModifyDate. The
/// packet identifies the level (pdfaid:part, pdfaid:conformance) and is stored unfiltered. /Producer and /Trapped leave
/// the information dictionary (PDFsharp rewrites /Producer when it saves); the packet names PDF_Pro as producer.
/// </summary>
internal static class PdfAMetadata
{
    public const string Producer = "PDF_Pro";

    private static readonly string[] Texts = ["/Title", "/Author", "/Subject", "/Keywords", "/Creator"];

    public static void Write(PdfDocument document, PdfALevel level, DateTimeOffset now)
    {
        var info = document.Info;
        var values = Texts.ToDictionary(k => k, k => Clean(Text(info.Elements[k])));
        var created = ParseDate(Text(info.Elements["/CreationDate"]));
        var modified = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Offset);

        foreach (var key in Texts.Concat(["/CreationDate", "/ModDate", "/Producer", "/Trapped"]))
        {
            info.Elements.Remove(key);
        }

        foreach (var (key, value) in values.Where(v => v.Value.Length > 0))
        {
            info.Elements[key] = value.All(c => c < 128) ? new PdfString(value) : new PdfString(value, PdfStringEncoding.Unicode);
        }

        if (created is { } creation)
        {
            info.Elements["/CreationDate"] = new PdfString(InfoDate(creation));
        }

        info.Elements["/ModDate"] = new PdfString(InfoDate(modified));

        var packet = Packet(level, values, created, modified);
        var stream = new PdfDictionary(document);
        stream.Elements.SetName("/Type", "/Metadata");
        stream.Elements.SetName("/Subtype", "/XML");
        stream.CreateStream(Encoding.UTF8.GetBytes(packet));
        document.Internals.AddObject(stream);
        document.Internals.Catalog.Elements["/Metadata"] = stream.Reference!;
    }

    /// <summary>A PDF date ("D:YYYYMMDDHHmmSSOHH'mm'", parts after the year optional), or null.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        var digits = 0;
        while (digits < s.Length && digits < 14 && char.IsAsciiDigit(s[digits]))
        {
            digits++;
        }

        if (digits < 4 || digits % 2 != 0)
        {
            return null;
        }

        int Part(int at, int fallback) => at + 2 <= digits ? int.Parse(s.AsSpan(at, 2), CultureInfo.InvariantCulture) : fallback;
        var year = int.Parse(s.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var (month, day, hour, minute, second) = (Part(4, 1), Part(6, 1), Part(8, 0), Part(10, 0), Part(12, 0));
        var offset = TimeSpan.Zero;
        var rest = s[digits..].Replace("'", string.Empty, StringComparison.Ordinal);
        if (rest.Length > 0 && rest[0] is '+' or '-')
        {
            var hours = rest.Length >= 3 && int.TryParse(rest.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;
            var minutes = rest.Length >= 5 && int.TryParse(rest.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
            offset = new TimeSpan(hours, minutes, 0) * (rest[0] == '-' ? -1 : 1);
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>"D:20261110143005+07'00'".</summary>
    public static string InfoDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"D:{date:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'");
    }

    /// <summary>"2026-11-10T14:30:05+07:00".</summary>
    public static string XmpDate(DateTimeOffset date) => date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string Packet(PdfALevel level, IReadOnlyDictionary<string, string> values, DateTimeOffset? created, DateTimeOffset modified)
    {
        static string X(string text) => SecurityElement.Escape(text);
        var xmp = new StringBuilder();
        xmp.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        xmp.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        xmp.Append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        xmp.Append("  <rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
        xmp.Append(CultureInfo.InvariantCulture, $"   <pdfaid:part>{(level == PdfALevel.A1B ? 1 : 2)}</pdfaid:part>\n");
        xmp.Append("   <pdfaid:conformance>B</pdfaid:conformance>\n");
        xmp.Append("  </rdf:Description>\n");
        xmp.Append("  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        xmp.Append("   <dc:format>application/pdf</dc:format>\n");
        if (values["/Title"] is { Length: > 0 } title)
        {
            xmp.Append(CultureInfo.InvariantCulture, $"   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{X(title)}</rdf:li></rdf:Alt></dc:title>\n");
        }

        if (values["/Author"] is { Length: > 0 } author)
        {
            xmp.Append(CultureInfo.InvariantCulture, $"   <dc:creator><rdf:Seq><rdf:li>{X(author)}</rdf:li></rdf:Seq></dc:creator>\n");
        }

        if (values["/Subject"] is { Length: > 0 } subject)
        {
            xmp.Append(CultureInfo.InvariantCulture, $"   <dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{X(subject)}</rdf:li></rdf:Alt></dc:description>\n");
        }

        xmp.Append("  </rdf:Description>\n");
        xmp.Append("  <rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
        if (values["/Creator"] is { Length: > 0 } creator)
        {
            xmp.Append(CultureInfo.InvariantCulture, $"   <xmp:CreatorTool>{X(creator)}</xmp:CreatorTool>\n");
        }

        if (created is { } creation)
        {
            xmp.Append(CultureInfo.InvariantCulture, $"   <xmp:CreateDate>{XmpDate(creation)}</xmp:CreateDate>\n");
        }

        xmp.Append(CultureInfo.InvariantCulture, $"   <xmp:ModifyDate>{XmpDate(modified)}</xmp:ModifyDate>\n");
        xmp.Append(CultureInfo.InvariantCulture, $"   <xmp:MetadataDate>{XmpDate(modified)}</xmp:MetadataDate>\n");
        xmp.Append("  </rdf:Description>\n");
        xmp.Append("  <rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        xmp.Append(CultureInfo.InvariantCulture, $"   <pdf:Producer>{Producer}</pdf:Producer>\n");
        if (values["/Keywords"] is { Length: > 0 } keywords)
        {
            xmp.Append(CultureInfo.InvariantCulture, $"   <pdf:Keywords>{X(keywords)}</pdf:Keywords>\n");
        }

        xmp.Append("  </rdf:Description>\n");
        xmp.Append(" </rdf:RDF>\n");
        xmp.Append("</x:xmpmeta>\n");
        xmp.Append("<?xpacket end=\"w\"?>");
        return xmp.ToString();
    }

    private static string? Text(PdfItem? item) => Resolve(item) is PdfString text ? text.Value : null;

    /// <summary>Composed (NFC), without characters XML cannot carry, trimmed.</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var value = rune.Value;
            if (value is 0x9 or 0xA or 0xD || (value >= 0x20 && value is not (0xFFFE or 0xFFFF)))
            {
                kept.Append(value is 0x9 or 0xA or 0xD ? " " : rune.ToString());
            }
        }

        return kept.ToString().Trim();
    }
}

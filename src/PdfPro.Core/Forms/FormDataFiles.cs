using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PdfPro.Core.Forms;

/// <summary>A form data file that cannot be read (not JSON or XFDF, or not shaped as form data).</summary>
public sealed class FormDataFormatException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Form data as JSON: one object whose properties are the fields — a string, or an array for the choices of a list;
/// qualified names may also be nested objects ({"a": {"b": "x"}} is "a.b"). Numbers and true/false are read as text;
/// null clears a field.
/// </summary>
public static class FormDataJson
{
    /// <summary>Most entries read from one file (defends against hostile files).</summary>
    public const int MaxEntries = 20_000;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Vietnamese stays readable; the file is not HTML
    };

    public static string Write(IReadOnlyList<FormDataEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var entry in entries)
            {
                if (entry.IsList)
                {
                    writer.WriteStartArray(entry.Name);
                    foreach (var value in entry.Values)
                    {
                        writer.WriteStringValue(value);
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString(entry.Name, entry.Values.FirstOrDefault() ?? string.Empty);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <exception cref="FormDataFormatException">The text is not a JSON object of field values.</exception>
    public static IReadOnlyList<FormDataEntry> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormDataFormatException("Form data must be a JSON object.");
            }

            var entries = new List<FormDataEntry>();
            Read(document.RootElement, string.Empty, entries);
            return entries;
        }
        catch (JsonException ex)
        {
            throw new FormDataFormatException("The JSON cannot be read.", ex);
        }
    }

    private static void Read(JsonElement element, string prefix, List<FormDataEntry> entries)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (entries.Count >= MaxEntries)
            {
                throw new FormDataFormatException($"Form data holds at most {MaxEntries} values.");
            }

            var name = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Read(property.Value, name, entries);
                    break;
                case JsonValueKind.Array:
                    entries.Add(new FormDataEntry(name, [.. property.Value.EnumerateArray().Take(MaxEntries).Select(Text)], IsList: true));
                    break;
                default:
                    entries.Add(new FormDataEntry(name, [Text(property.Value)]));
                    break;
            }
        }
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => throw new FormDataFormatException("A field's value must be text, a number, true/false, null or a list of them."),
    };
}

/// <summary>
/// Form data as XFDF (ISO 19444-1), the format Acrobat exports: &lt;fields&gt; with nested &lt;field name&gt; for qualified
/// names and one &lt;value&gt; per value (several for the choices of a list). Read without DTDs or external entities.
/// </summary>
public static class FormXfdf
{
    public const string Namespace = "http://ns.adobe.com/xfdf/";

    /// <summary>The largest XFDF read (characters).</summary>
    private const long MaxCharacters = 50_000_000;

    /// <param name="entries">The values.</param>
    /// <param name="fileName">The PDF the data belongs to (written as &lt;f href&gt;), or null.</param>
    public static string Write(IReadOnlyList<FormDataEntry> entries, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(entries);
        XNamespace ns = Namespace;
        var fields = new XElement(ns + "fields");
        foreach (var entry in entries)
        {
            var parent = fields;
            var parts = entry.Name.Split('.');
            foreach (var part in parts[..^1])
            {
                var existing = parent.Elements(ns + "field").FirstOrDefault(f => (string?)f.Attribute("name") == part && !f.Elements(ns + "value").Any());
                if (existing is null)
                {
                    existing = new XElement(ns + "field", new XAttribute("name", part));
                    parent.Add(existing);
                }

                parent = existing;
            }

            parent.Add(new XElement(ns + "field", new XAttribute("name", parts[^1]), entry.Values.Select(v => new XElement(ns + "value", v))));
        }

        var root = new XElement(ns + "xfdf", new XAttribute(XNamespace.Xml + "space", "preserve"));
        if (!string.IsNullOrEmpty(fileName))
        {
            root.Add(new XElement(ns + "f", new XAttribute("href", fileName)));
        }

        root.Add(fields);
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) }))
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <exception cref="FormDataFormatException">The text is not XFDF.</exception>
    public static IReadOnlyList<FormDataEntry> Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxCharacters,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormDataFormatException("The XFDF cannot be read.", ex);
        }

        if (document.Root is not { Name.LocalName: "xfdf" } root)
        {
            throw new FormDataFormatException("The file is not XFDF.");
        }

        var entries = new List<FormDataEntry>();
        foreach (var fields in root.Elements().Where(e => e.Name.LocalName == "fields"))
        {
            Read(fields, string.Empty, entries, 0);
        }

        return entries;
    }

    private static void Read(XElement parent, string prefix, List<FormDataEntry> entries, int depth)
    {
        if (depth > 32)
        {
            throw new FormDataFormatException("The XFDF fields are nested too deeply.");
        }

        foreach (var field in parent.Elements().Where(e => e.Name.LocalName == "field"))
        {
            if (entries.Count >= FormDataJson.MaxEntries)
            {
                throw new FormDataFormatException($"Form data holds at most {FormDataJson.MaxEntries} values.");
            }

            var part = (string?)field.Attribute("name") ?? string.Empty;
            var name = prefix.Length == 0 ? part : part.Length == 0 ? prefix : prefix + "." + part;
            var values = field.Elements().Where(e => e.Name.LocalName == "value").Select(v => v.Value).ToList();
            if (values.Count > 0 || !field.Elements().Any(e => e.Name.LocalName == "field"))
            {
                entries.Add(new FormDataEntry(name, values.Count == 0 ? [string.Empty] : values, IsList: values.Count > 1));
            }

            Read(field, name, entries, depth + 1);
        }
    }
}

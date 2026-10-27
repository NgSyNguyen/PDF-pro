using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Forms;

/// <summary>
/// Reads the AcroForm of a document at the dictionary level (12.7): the field tree under /AcroForm /Fields with
/// inherited entries (/FT, /Ff, /V, /DV, /DA, /Q, /Opt, /MaxLen), widgets merged into their field or listed as /Kids,
/// and the page of every widget (/P, or the page whose /Annots holds it).
/// </summary>
public static partial class FormReader
{
    /// <summary>Upper bound for the fields and the depth of the tree (defends against hostile files).</summary>
    private const int MaxFields = 20_000;
    private const int MaxDepth = 32;

    private static readonly string[] Inherited = ["/FT", "/Ff", "/V", "/DV", "/DA", "/Q", "/Opt", "/MaxLen"];

    /// <summary>The document's fields in /Fields order (depth first); empty without a form.</summary>
    public static IReadOnlyList<FormField> Read(PdfDocument document)
        => Fields(document).Select(f => f.Field).ToList();

    /// <summary>The fields with their dictionaries and widget dictionaries, for changing them.</summary>
    internal static IReadOnlyList<(FormField Field, PdfDictionary Dictionary, IReadOnlyList<PdfDictionary> Widgets)> Fields(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<(FormField, PdfDictionary, IReadOnlyList<PdfDictionary>)>();
        if (AsDictionary(document.Internals.Catalog.Elements["/AcroForm"]) is not { } form || AsArray(form.Elements["/Fields"]) is not { } roots)
        {
            return result;
        }

        var pages = PagesOfWidgets(document);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots.Elements.Select(AsDictionary).OfType<PdfDictionary>())
        {
            Walk(root, string.Empty, new Dictionary<string, PdfItem>(), 0, pages, seen, result);
        }

        return result;
    }

    /// <summary>The form type: none, AcroForm, or XFA (with or without AcroForm fields).</summary>
    public static FormType TypeOf(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (AsDictionary(document.Internals.Catalog.Elements["/AcroForm"]) is not { } form)
        {
            return FormType.None;
        }

        if (form.Elements.ContainsKey("/XFA"))
        {
            return AsArray(form.Elements["/Fields"]) is { Elements.Count: > 0 } ? FormType.XfaForeground : FormType.XfaFull;
        }

        return FormType.AcroForm;
    }

    private static void Walk(
        PdfDictionary node,
        string parentName,
        Dictionary<string, PdfItem> inherited,
        int depth,
        Dictionary<PdfDictionary, int> pages,
        HashSet<PdfDictionary> seen,
        List<(FormField, PdfDictionary, IReadOnlyList<PdfDictionary>)> result)
    {
        if (depth > MaxDepth || result.Count >= MaxFields || !seen.Add(node))
        {
            return;
        }

        var partial = node.Elements.GetString("/T");
        var name = partial.Length == 0 ? parentName : parentName.Length == 0 ? partial : parentName + "." + partial;
        var values = new Dictionary<string, PdfItem>(inherited);
        foreach (var key in Inherited)
        {
            if (node.Elements[key] is { } item)
            {
                values[key] = item;
            }
        }

        var kids = AsArray(node.Elements["/Kids"])?.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList() ?? [];
        var childFields = kids.Where(k => k.Elements.ContainsKey("/T")).ToList();
        if (childFields.Count > 0)
        {
            foreach (var child in childFields)
            {
                Walk(child, name, values, depth + 1, pages, seen, result);
            }

            return;
        }

        // A terminal field: its widgets are its /Kids, or itself when field and widget are merged.
        var widgets = kids.Count > 0 ? kids : Name(node, "/Subtype") == "/Widget" || node.Elements.ContainsKey("/Rect") ? [node] : [];
        if (name.Length == 0)
        {
            return;
        }

        result.Add((Field(name, values, node, widgets, pages), node, widgets));
    }

    private static FormField Field(string name, Dictionary<string, PdfItem> values, PdfDictionary node, List<PdfDictionary> widgets, Dictionary<PdfDictionary, int> pages)
    {
        var flags = (FormFieldFlags)(values.TryGetValue("/Ff", out var ff) && Resolve(ff) is PdfInteger number ? number.Value : 0);
        var type = values.TryGetValue("/FT", out var ft) && Resolve(ft) is PdfName typeName ? typeName.Value : string.Empty;
        var kind = type switch
        {
            "/Btn" when flags.HasFlag(FormFieldFlags.PushButton) => FormFieldKind.PushButton,
            "/Btn" when flags.HasFlag(FormFieldFlags.Radio) => FormFieldKind.RadioGroup,
            "/Btn" => FormFieldKind.CheckBox,
            "/Ch" when flags.HasFlag(FormFieldFlags.Combo) => FormFieldKind.ComboBox,
            "/Ch" => FormFieldKind.ListBox,
            "/Sig" => FormFieldKind.Signature,
            _ => FormFieldKind.Text,
        };
        var options = Options(values.GetValueOrDefault("/Opt"));
        var (value, selected) = kind == FormFieldKind.PushButton
            ? (widgets.Select(w => AsDictionary(w.Elements["/MK"]) is { } look ? Text(look.Elements["/CA"]) : null).FirstOrDefault(c => c is not null), (IReadOnlyList<string>)[])
            : Value(kind, values.GetValueOrDefault("/V"), options);
        var formWidgets = widgets.Select(w => new FormWidget(
            pages.GetValueOrDefault(w, -1),
            Rect(w),
            kind is FormFieldKind.CheckBox or FormFieldKind.RadioGroup ? OnState(w) : null)).ToList();
        var (_, size) = Appearance(values.GetValueOrDefault("/DA"));
        return new FormField(
            name,
            kind,
            value,
            selected,
            flags,
            options,
            formWidgets,
            node.Elements.ContainsKey("/TU") ? node.Elements.GetString("/TU") : null,
            values.TryGetValue("/MaxLen", out var max) && Resolve(max) is PdfInteger length ? length.Value : 0,
            size,
            values.TryGetValue("/Q", out var q) && Resolve(q) is PdfInteger quadding && Enum.IsDefined((FormTextAlignment)quadding.Value) ? (FormTextAlignment)quadding.Value : FormTextAlignment.Left,
            Text(values.GetValueOrDefault("/DV")),
            kind == FormFieldKind.Text ? Format(node) : null,
            kind is FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox or FormFieldKind.PushButton ? widgets.Select(Family).FirstOrDefault(f => f is not null) : null);
    }

    /// <summary>The number or date format of a text field, from its /AA format action (Acrobat's AF functions).</summary>
    private static FormTextFormat? Format(PdfDictionary field)
    {
        if (AsDictionary(field.Elements["/AA"]) is not { } actions || AsDictionary(actions.Elements["/F"]) is not { } action || Script(action.Elements["/JS"]) is not { } script)
        {
            return null;
        }

        if (NumberFormat().Match(script) is { Success: true } number
            && int.TryParse(number.Groups["dec"].Value, CultureInfo.InvariantCulture, out var decimals)
            && int.TryParse(number.Groups["sep"].Value, CultureInfo.InvariantCulture, out var separators))
        {
            return FormTextFormat.Number(Math.Clamp(decimals, 0, FormTextFormat.MaxDecimals), separators is 2 or 3 ? NumberSeparators.DotComma : NumberSeparators.CommaDot);
        }

        return DateFormat().Match(script) is { Success: true } date ? FormTextFormat.Date(date.Groups["pattern"].Value) : null;
    }

    /// <summary>The text of a /JS entry: a string, or a stream (at most 64 KB read).</summary>
    private static string? Script(PdfItem? item) => Resolve(item) switch
    {
        PdfString text => text.Value,
        PdfDictionary { Stream: { } stream } => Encoding.Latin1.GetString(stream.UnfilteredValue.AsSpan(0, Math.Min(stream.UnfilteredValue.Length, 65_536))),
        _ => null,
    };

    /// <summary>The bundled family a widget's value is drawn in, from the fonts of its appearance.</summary>
    private static string? Family(PdfDictionary widget)
    {
        if (AsDictionary(widget.Elements["/AP"]) is not { } appearance || AsDictionary(appearance.Elements["/N"]) is not { } normal
            || AsDictionary(normal.Elements["/Resources"]) is not { } resources || AsDictionary(resources.Elements["/Font"]) is not { } fonts)
        {
            return null;
        }

        foreach (var font in fonts.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>())
        {
            var name = new string([.. font.Elements.GetName("/BaseFont").Where(char.IsLetter)]);
            if (FontFamilies.Bundled.FirstOrDefault(f => name.Contains(f.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)) is { } family)
            {
                return family;
            }
        }

        return null;
    }

    [GeneratedRegex(@"AFNumber_Format\(\s*(?<dec>[0-9]+)\s*,\s*(?<sep>[0-9]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberFormat();

    [GeneratedRegex(@"AFDate_FormatEx\(\s*[""'](?<pattern>[^""']{1,40})[""']", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DateFormat();

    private static (string? Value, IReadOnlyList<string> Selected) Value(FormFieldKind kind, PdfItem? item, IReadOnlyList<FormOption> options)
    {
        var resolved = Resolve(item);
        switch (kind)
        {
            case FormFieldKind.CheckBox or FormFieldKind.RadioGroup:
                return (resolved is PdfName name ? name.Value.TrimStart('/') : resolved is PdfString text ? text.Value : FormField.Off, []);
            case FormFieldKind.ListBox:
                var chosen = resolved is PdfArray array ? array.Elements.Select(Text).OfType<string>().ToList() : Text(resolved) is { } one ? [one] : [];
                return (chosen.FirstOrDefault(), chosen);
            default:
                return (Text(resolved), []);
        }
    }

    /// <summary>/Opt: export values with their display text (pairs), or plain strings.</summary>
    private static IReadOnlyList<FormOption> Options(PdfItem? item)
    {
        if (AsArray(item) is not { } options)
        {
            return [];
        }

        var result = new List<FormOption>();
        foreach (var option in options.Elements.Take(MaxFields))
        {
            if (AsArray(option) is { Elements.Count: >= 2 } pair && Text(pair.Elements[0]) is { } export)
            {
                result.Add(new FormOption(export, Text(pair.Elements[1]) ?? export));
            }
            else if (Text(option) is { } plain)
            {
                result.Add(new FormOption(plain, plain));
            }
        }

        return result;
    }

    /// <summary>The appearance state of a check box or radio button that is not "Off".</summary>
    internal static string? OnState(PdfDictionary widget)
        => AsDictionary(widget.Elements["/AP"]) is { } appearance && AsDictionary(appearance.Elements["/N"]) is { } normal
            ? normal.Elements.Keys.Select(k => k.TrimStart('/')).FirstOrDefault(k => k != FormField.Off)
            : null;

    /// <summary>The font resource name and size of a /DA string ("/Helv 10 Tf 0 g").</summary>
    internal static (string? Font, double Size) Appearance(PdfItem? item)
    {
        if (Text(item) is not { } da)
        {
            return (null, 0);
        }

        var tokens = da.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var at = Array.LastIndexOf(tokens, "Tf");
        return at >= 2 && double.TryParse(tokens[at - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
            ? (tokens[at - 2], size)
            : (null, 0);
    }

    internal static PdfRect Rect(PdfDictionary widget)
        => AsArray(widget.Elements["/Rect"]) is { Elements.Count: 4 } rect
            ? new PdfRect(Number(rect.Elements[0]), Number(rect.Elements[1]), Number(rect.Elements[2]), Number(rect.Elements[3]))
            : default;

    internal static string? Text(PdfItem? item) => Resolve(item) switch
    {
        PdfString text => text.Value,
        PdfName name => name.Value.TrimStart('/'),
        _ => null,
    };

    private static double Number(PdfItem item) => Resolve(item) switch
    {
        PdfReal real => real.Value,
        PdfInteger integer => integer.Value,
        PdfLongInteger integer => integer.Value,
        _ => 0,
    };

    /// <summary>The page of every widget annotation on a page (by /Annots).</summary>
    private static Dictionary<PdfDictionary, int> PagesOfWidgets(PdfDocument document)
    {
        var pages = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < document.PageCount; i++)
        {
            if (Annotations(document.Pages[i]) is { } annotations)
            {
                foreach (var annotation in annotations.Elements.Select(AsDictionary).OfType<PdfDictionary>())
                {
                    pages.TryAdd(annotation, i);
                }
            }
        }

        return pages;
    }
}

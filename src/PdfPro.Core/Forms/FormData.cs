using PdfPro.Core.Text;

namespace PdfPro.Core.Forms;

/// <summary>A value for a field as found in a data file (JSON, XFDF, a row of a table), before it is matched to the form.</summary>
/// <param name="Name">The field's fully qualified name, as written in the data.</param>
/// <param name="Values">One value, or the choices of a list (empty clears the field).</param>
/// <param name="IsList">The data gave a list (a JSON array, several XFDF values).</param>
public sealed record FormDataEntry(string Name, IReadOnlyList<string> Values, bool IsList = false)
{
    public bool Equals(FormDataEntry? other)
        => other is not null && Name == other.Name && IsList == other.IsList && Values.SequenceEqual(other.Values);

    public override int GetHashCode() => HashCode.Combine(Name, Values.Count, IsList);
}

/// <summary>Values matched to the form: those to give, the names the form does not have, the values that do not fit their field.</summary>
public sealed record FormDataMatch(IReadOnlyList<FormValue> Values, IReadOnlyList<string> Unknown, IReadOnlyList<string> Rejected);

/// <summary>
/// Form data in and out: the values of the fillable fields as entries (for JSON and XFDF), and entries matched to the
/// fields of a form — names exactly, else ignoring case and accents' encoding (NFC); a check box takes its on value or
/// yes/no words ("x", "1", "true", "có"…); a radio group one of its buttons; a list its choices by value or by the text
/// shown; a number or date field a value of its format.
/// </summary>
public static class FormData
{
    private static readonly string[] Yes = ["x", "1", "true", "yes", "y", "on", "có", "co", "đúng", "dung", "checked", "✓", "✔"];
    private static readonly string[] No = ["", "0", "false", "no", "n", "off", "không", "khong", "sai", "unchecked"];

    /// <summary>The values to export: every field someone fills (not buttons, signatures or fields marked not to export).</summary>
    /// <param name="fields">The form's fields.</param>
    /// <param name="valueOf">A field's value as shown now.</param>
    public static IReadOnlyList<FormDataEntry> Entries(IReadOnlyList<FormField> fields, Func<FormField, FormValue> valueOf)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(valueOf);
        var entries = new List<FormDataEntry>();
        foreach (var field in fields.Where(f => f.Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature) && !f.Flags.HasFlag(FormFieldFlags.NoExport)))
        {
            var value = valueOf(field);
            entries.Add(field.Kind == FormFieldKind.ListBox
                ? new FormDataEntry(field.Name, [.. value.Selected ?? []], IsList: true)
                : new FormDataEntry(field.Name, [field.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioGroup && string.IsNullOrEmpty(value.Value) ? FormField.Off : value.Value]));
        }

        return entries;
    }

    /// <summary>Matches data to the fields of a form (see the class).</summary>
    public static FormDataMatch Match(IReadOnlyList<FormField> fields, IReadOnlyList<FormDataEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(entries);
        var values = new Dictionary<string, FormValue>(StringComparer.Ordinal);
        var unknown = new List<string>();
        var rejected = new List<string>();
        foreach (var entry in entries)
        {
            if (Find(fields, entry.Name) is not { } field)
            {
                unknown.Add(entry.Name);
                continue;
            }

            if (!field.IsFillable || Value(field, entry.Values) is not { } value)
            {
                rejected.Add(entry.Name);
                continue;
            }

            values[field.Name] = value;
        }

        return new FormDataMatch([.. values.Values], unknown, rejected);
    }

    /// <summary>The field a name in the data means: the same name, else the same name ignoring case (NFC).</summary>
    public static FormField? Find(IReadOnlyList<FormField> fields, string name)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(name);
        return fields.FirstOrDefault(f => f.Name == name) ?? fields.FirstOrDefault(f => Same(f.Name, name));
    }

    /// <summary>The value a field gets from data, or null when the data does not fit it.</summary>
    public static FormValue? Value(FormField field, IReadOnlyList<string> data)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(data);
        var first = TextNormalizer.ToNfc(data.FirstOrDefault() ?? string.Empty);
        switch (field.Kind)
        {
            case FormFieldKind.Text:
                var text = first;
                if (field.Format is { } format && !format.TryNormalize(first, out text))
                {
                    return null;
                }

                return new FormValue(field.Name, field.MaxLength > 0 && text.Length > field.MaxLength ? text[..field.MaxLength] : text);
            case FormFieldKind.CheckBox:
                var on = field.Widgets.Select(w => w.OnState).OfType<string>().FirstOrDefault() ?? "Yes";
                var word = first.Trim();
                if (Same(word, on) || Yes.Contains(word, StringComparer.OrdinalIgnoreCase))
                {
                    return new FormValue(field.Name, on);
                }

                return Same(word, FormField.Off) || No.Contains(word, StringComparer.OrdinalIgnoreCase) ? new FormValue(field.Name, FormField.Off) : null;
            case FormFieldKind.RadioGroup:
                if (first.Trim().Length == 0 || Same(first.Trim(), FormField.Off))
                {
                    return new FormValue(field.Name, FormField.Off);
                }

                return field.Widgets.Select(w => w.OnState).OfType<string>().FirstOrDefault(s => Same(s, first.Trim())) is { } state
                    ? new FormValue(field.Name, state)
                    : null;
            case FormFieldKind.ComboBox:
                if (first.Length == 0)
                {
                    return new FormValue(field.Name, string.Empty);
                }

                return Choice(field, first) is { } choice
                    ? new FormValue(field.Name, choice)
                    : field.Flags.HasFlag(FormFieldFlags.Edit) ? new FormValue(field.Name, first) : null;
            case FormFieldKind.ListBox:
                var chosen = new List<string>();
                foreach (var item in data.Select(TextNormalizer.ToNfc).Where(d => d.Length > 0))
                {
                    if (Choice(field, item) is not { } export)
                    {
                        return null;
                    }

                    chosen.Add(export);
                }

                if (chosen.Count > 1 && !field.Flags.HasFlag(FormFieldFlags.MultiSelect))
                {
                    return null;
                }

                return new FormValue(field.Name, chosen.FirstOrDefault() ?? string.Empty, chosen);
            default:
                return null;
        }
    }

    /// <summary>The export value of the choice a data value means (its export value or its text, ignoring case).</summary>
    private static string? Choice(FormField field, string value)
        => field.Options.FirstOrDefault(o => o.Export == value || o.Display == value)?.Export
            ?? field.Options.FirstOrDefault(o => Same(o.Export, value) || Same(o.Display, value))?.Export;

    private static bool Same(string a, string b)
        => string.Equals(TextNormalizer.ToNfc(a), TextNormalizer.ToNfc(b), StringComparison.OrdinalIgnoreCase);
}

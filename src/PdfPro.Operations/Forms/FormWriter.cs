using System.Globalization;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Forms;

/// <summary>
/// A field to place on a page (forms.design). Radio buttons of one group share the group's <see cref="Name"/>, each
/// with its own <see cref="OnState"/>.
/// </summary>
/// <param name="Name">Fully qualified name (unique, except for the buttons of a radio group).</param>
/// <param name="Kind">What it is.</param>
/// <param name="Page">0-based page.</param>
/// <param name="Rect">Its box, in the page's PDF user space.</param>
/// <param name="Flags">Required, read-only, multi-line, password, comb, editable combo, multi-select…</param>
/// <param name="Value">The starting value, also the default one /DV (text, a choice, the on state when checked).</param>
/// <param name="Options">The choices of a combo or list box.</param>
/// <param name="ToolTip">Shown when hovering, read by screen readers (/TU).</param>
/// <param name="FontSize">Text size, 0 to fit the box.</param>
/// <param name="Alignment">Where the text goes in the box.</param>
/// <param name="MaxLength">Most characters of a text field (0: no limit; needed for comb fields).</param>
/// <param name="OnState">The value of a check box or radio button when on ("Yes" by default).</param>
/// <param name="Caption">The words of a push button.</param>
/// <param name="Format">The number or date format of a text field.</param>
/// <param name="FontFamily">The bundled family the value is drawn in (null: the default, Noto Sans).</param>
public sealed record FormFieldSpec(
    string Name,
    FormFieldKind Kind,
    int Page,
    PdfRect Rect,
    FormFieldFlags Flags = FormFieldFlags.None,
    string? Value = null,
    IReadOnlyList<FormOption>? Options = null,
    string? ToolTip = null,
    double FontSize = 0,
    FormTextAlignment Alignment = FormTextAlignment.Left,
    int MaxLength = 0,
    string? OnState = null,
    string? Caption = null,
    FormTextFormat? Format = null,
    string? FontFamily = null)
{
    public const string DefaultOnState = "Yes";

    /// <summary>What is wrong with the spec (English, for validation), or null.</summary>
    public string? Problem(int pageCount)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Split('.').Any(string.IsNullOrWhiteSpace) || Name.Length > 255)
        {
            return "A field needs a name (without a leading or trailing dot, at most 255 characters).";
        }

        if (Page < 0 || Page >= pageCount)
        {
            return $"The page must be between 1 and {pageCount}.";
        }

        if (Rect.Width < 2 || Rect.Height < 2)
        {
            return "The field's box is too small.";
        }

        if (FontSize is < 0 or > 200 || MaxLength < 0 || !Enum.IsDefined(Kind) || !Enum.IsDefined(Alignment))
        {
            return "The size, length, kind or alignment is not valid.";
        }

        if (Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox && (Options is null || Options.Count == 0))
        {
            return "A list needs choices.";
        }

        if (Flags.HasFlag(FormFieldFlags.Comb) && MaxLength == 0)
        {
            return "A comb field needs a most number of characters.";
        }

        if (Format is { Kind: not FormFormatKind.None } format && (Kind != FormFieldKind.Text || format.Problem() is not null))
        {
            return format.Problem() ?? "Only text fields have a number or date format.";
        }

        if (FontFamily is { } family && !FontFamilies.IsBundled(family))
        {
            return "The font must be one of the bundled families.";
        }

        return OnState is { } on && (on.Length == 0 || on == FormField.Off || on.Any(c => c <= ' ' || c is '/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%' or '#'))
            ? "The on value must be a plain name other than Off."
            : null;
    }
}

/// <summary>
/// Writes AcroForm fields at the dictionary level (12.7): new fields with their widgets (forms.design) and new values
/// (forms.fill), each widget given an appearance stream (<see cref="FormAppearances"/>) so that it shows the same in
/// every viewer. /DA names Helvetica from /DR (other editors use it when typing); the appearances themselves are
/// written in Noto Sans so that Vietnamese always shows. /NeedAppearances is cleared.
/// </summary>
public static class FormWriter
{
    /// <summary>Annotation flags (12.5.3): Print.</summary>
    private const int Print = 1 << 2;

    /// <summary>Places new fields; the buttons of a radio group (same name) become the kids of one field.</summary>
    /// <exception cref="ArgumentException">A spec is not valid, or a name is taken by a field of another kind.</exception>
    public static void AddFields(PdfDocument document, IReadOnlyList<FormFieldSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(specs);
        foreach (var spec in specs)
        {
            if (spec.Problem(document.PageCount) is { } problem)
            {
                throw new ArgumentException($"Field '{spec.Name}': {problem}", nameof(specs));
            }
        }

        var form = AcroForm(document);
        var fields = AsArray(form.Elements["/Fields"])!;
        var existing = FormReader.Fields(document).ToDictionary(f => f.Field.Name, f => f.Dictionary, StringComparer.Ordinal);
        foreach (var group in specs.GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Any(s => s.Kind != first.Kind) || (first.Kind != FormFieldKind.RadioGroup && (group.Count() > 1 || existing.ContainsKey(first.Name)))
                || existing.Keys.Concat(specs.Select(s => s.Name)).Any(other => IsAncestor(other, first.Name) || IsAncestor(first.Name, other)))
            {
                throw new ArgumentException($"The name '{first.Name}' is used by another field.", nameof(specs));
            }

            if (first.Kind == FormFieldKind.RadioGroup)
            {
                var parent = existing.TryGetValue(first.Name, out var found) ? found : NewField(document, fields, first, merged: false);
                var kids = AsArray(parent.Elements["/Kids"]) ?? new PdfArray(document);
                parent.Elements["/Kids"] = kids;
                foreach (var button in group)
                {
                    var widget = NewWidget(document, button);
                    widget.Elements["/Parent"] = parent.Reference!;
                    kids.Elements.Add(widget.Reference!);
                    if (button.Value is { } chosen && chosen == (button.OnState ?? FormFieldSpec.DefaultOnState))
                    {
                        parent.Elements.SetName("/V", "/" + chosen);
                        parent.Elements.SetName("/DV", "/" + chosen);
                    }
                }
            }
            else
            {
                var field = NewField(document, fields, first, merged: true);
                var value = first.Value is { } given ? TextNormalizer.ToNfc(given) : first.Kind == FormFieldKind.CheckBox ? FormField.Off : null;
                SetValue(field, first.Kind, value, first.Kind == FormFieldKind.ListBox && value is not null ? [value] : null, first.Options ?? []);
                if (first.Kind != FormFieldKind.PushButton && field.Elements["/V"] is { } start)
                {
                    field.Elements["/DV"] = start is PdfArray choices ? choices.Clone() : start;
                }
            }
        }

        RefreshAll(
            document,
            specs.Select(s => s.Name).ToHashSet(StringComparer.Ordinal),
            captions: specs.Where(s => s.Caption is not null).ToDictionary(s => s.Name, s => s.Caption!, StringComparer.Ordinal),
            families: specs.Where(s => s.FontFamily is not null).DistinctBy(s => s.Name).ToDictionary(s => s.Name, s => s.FontFamily!, StringComparer.Ordinal));
    }

    /// <summary>
    /// Takes fields off the form with their widgets (from the pages' /Annots), and their parents left without kids.
    /// </summary>
    /// <exception cref="InvalidDataException">A field is not in the document (any more).</exception>
    public static void RemoveFields(PdfDocument document, IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return;
        }

        var found = FormReader.Fields(document).Where(f => names.Contains(f.Field.Name)).ToList();
        if (names.Except(found.Select(f => f.Field.Name), StringComparer.Ordinal).FirstOrDefault() is { } missing)
        {
            throw new InvalidDataException($"The field '{missing}' is not in the document any more.");
        }

        var widgets = found.SelectMany(f => f.Widgets).ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < document.PageCount; i++)
        {
            if (Annotations(document.Pages[i]) is { } annotations && annotations.Elements.Any(a => widgets.Contains(AsDictionary(a))))
            {
                var own = AnnotationWriter.OwnAnnotations(document, document.Pages[i]);
                foreach (var item in own.Elements.Where(a => widgets.Contains(AsDictionary(a))).ToList())
                {
                    own.Elements.Remove(item);
                }
            }
        }

        var form = AsDictionary(document.Internals.Catalog.Elements["/AcroForm"])!;
        foreach (var (_, dictionary, _) in found)
        {
            Unlink(form, dictionary);
        }

        if (AsArray(form.Elements["/CO"]) is { } order)
        {
            var removed = found.Select(f => f.Dictionary).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var item in order.Elements.Where(e => removed.Contains(AsDictionary(e))).ToList())
            {
                order.Elements.Remove(item);
            }
        }
    }

    /// <summary>Takes a field out of its parent's /Kids (or /Fields); a parent left without kids goes too.</summary>
    private static void Unlink(PdfDictionary form, PdfDictionary field)
    {
        for (var depth = 0; depth < 32; depth++)
        {
            var parent = AsDictionary(field.Elements["/Parent"]);
            var siblings = parent is null ? AsArray(form.Elements["/Fields"]) : AsArray(parent.Elements["/Kids"]);
            if (siblings is null)
            {
                return;
            }

            foreach (var item in siblings.Elements.Where(e => ReferenceEquals(AsDictionary(e), field)).ToList())
            {
                siblings.Elements.Remove(item);
            }

            if (parent is null || siblings.Elements.Count > 0)
            {
                return;
            }

            field = parent;
        }
    }

    /// <summary>Gives fields new values and redraws their widgets. Returns the names of fields that do not exist.</summary>
    public static IReadOnlyList<string> SetValues(PdfDocument document, IReadOnlyList<FormValue> values)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(values);
        var fields = FormReader.Fields(document).ToDictionary(f => f.Field.Name, StringComparer.Ordinal);
        var missing = new List<string>();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!fields.TryGetValue(value.Name, out var field) || !field.Field.IsFillable && field.Field.Kind != FormFieldKind.Signature)
            {
                missing.Add(value.Name);
                continue;
            }

            SetValue(field.Dictionary, field.Field.Kind, TextNormalizer.ToNfc(value.Value), value.Selected?.Select(TextNormalizer.ToNfc).ToList(), field.Field.Options);
            changed.Add(value.Name);
        }

        RefreshAll(document, changed, captions: new Dictionary<string, string>(StringComparer.Ordinal));
        return missing;
    }

    /// <summary>The document's /AcroForm, made when missing, with Helvetica and ZapfDingbats in /DR.</summary>
    internal static PdfDictionary AcroForm(PdfDocument document)
    {
        var catalog = document.Internals.Catalog;
        if (AsDictionary(catalog.Elements["/AcroForm"]) is not { } form)
        {
            form = new PdfDictionary(document);
            document.Internals.AddObject(form);
            catalog.Elements["/AcroForm"] = form.Reference!;
        }

        if (AsArray(form.Elements["/Fields"]) is null)
        {
            form.Elements["/Fields"] = new PdfArray(document);
        }

        var resources = AsDictionary(form.Elements["/DR"]) ?? new PdfDictionary(document);
        form.Elements["/DR"] = resources;
        var fonts = AsDictionary(resources.Elements["/Font"]) ?? new PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        if (!fonts.Elements.ContainsKey("/Helv"))
        {
            fonts.Elements["/Helv"] = StandardFont(document, "/Helvetica", winAnsi: true);
        }

        if (!fonts.Elements.ContainsKey("/ZaDb"))
        {
            fonts.Elements["/ZaDb"] = StandardFont(document, "/ZapfDingbats", winAnsi: false);
        }

        if (!form.Elements.ContainsKey("/DA"))
        {
            form.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
        }

        form.Elements.Remove("/NeedAppearances");
        return form;
    }

    private static PdfReference StandardFont(PdfDocument document, string name, bool winAnsi)
    {
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", name);
        if (winAnsi)
        {
            font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        }

        document.Internals.AddObject(font);
        return font.Reference!;
    }

    /// <summary>A new field in /Fields: merged with its widget (one place), or a parent for radio buttons.</summary>
    private static PdfDictionary NewField(PdfDocument document, PdfArray fields, FormFieldSpec spec, bool merged)
    {
        var field = merged ? NewWidget(document, spec) : new PdfDictionary(document);
        if (!merged)
        {
            document.Internals.AddObject(field);
        }

        // "a.b.c": the partial name "c" under parents "a" and "a.b" (found or made); a partial name has no dot.
        var dot = spec.Name.LastIndexOf('.');
        field.Elements["/T"] = Text(dot < 0 ? spec.Name : spec.Name[(dot + 1)..]);
        var siblings = fields;
        if (dot > 0)
        {
            var parent = Parent(document, fields, spec.Name[..dot]);
            field.Elements["/Parent"] = parent.Reference!;
            siblings = AsArray(parent.Elements["/Kids"])!;
        }

        var (type, flags) = spec.Kind switch
        {
            FormFieldKind.Text => ("/Tx", spec.Flags),
            FormFieldKind.CheckBox => ("/Btn", spec.Flags & ~(FormFieldFlags.Radio | FormFieldFlags.PushButton)),
            FormFieldKind.RadioGroup => ("/Btn", spec.Flags | FormFieldFlags.Radio | FormFieldFlags.NoToggleToOff),
            FormFieldKind.PushButton => ("/Btn", (spec.Flags | FormFieldFlags.PushButton) & ~FormFieldFlags.Radio),
            FormFieldKind.ComboBox => ("/Ch", spec.Flags | FormFieldFlags.Combo),
            FormFieldKind.ListBox => ("/Ch", spec.Flags & ~FormFieldFlags.Combo),
            _ => ("/Sig", spec.Flags),
        };
        field.Elements.SetName("/FT", type);
        field.Elements.SetInteger("/Ff", (int)flags); // also when 0: /Ff is inherited from parents

        if (spec.Kind is FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox)
        {
            field.Elements["/DA"] = new PdfString(string.Create(CultureInfo.InvariantCulture, $"/Helv {Math.Round(spec.FontSize, 2)} Tf 0 g"));
            if (spec.Alignment != FormTextAlignment.Left)
            {
                field.Elements.SetInteger("/Q", (int)spec.Alignment);
            }
        }

        if (spec.MaxLength > 0)
        {
            field.Elements.SetInteger("/MaxLen", spec.MaxLength);
        }

        if (spec.Kind == FormFieldKind.Text && spec.Format is { Kind: not FormFormatKind.None } format)
        {
            field.Elements["/AA"] = FormatActions(document, format);
        }

        if (spec.ToolTip is { Length: > 0 } tip)
        {
            field.Elements["/TU"] = Text(tip);
        }

        if (spec.Options is { Count: > 0 } options)
        {
            var array = new PdfArray(document);
            foreach (var option in options)
            {
                if (option.Export == option.Display)
                {
                    array.Elements.Add(Text(option.Export));
                }
                else
                {
                    var pair = new PdfArray(document);
                    pair.Elements.Add(Text(option.Export));
                    pair.Elements.Add(Text(option.Display));
                    array.Elements.Add(pair);
                }
            }

            field.Elements["/Opt"] = array;
        }

        if (spec.Kind == FormFieldKind.RadioGroup)
        {
            field.Elements.SetName("/V", "/" + FormField.Off);
        }

        siblings.Elements.Add(field.Reference!);
        return field;
    }

    /// <summary>"a" is an ancestor of "a.b": a field cannot also be the parent of another.</summary>
    private static bool IsAncestor(string name, string of)
        => of.Length > name.Length && of[name.Length] == '.' && of.StartsWith(name, StringComparison.Ordinal);

    /// <summary>The non-terminal field of a qualified name ("a.b"), made with its own parents when missing.</summary>
    private static PdfDictionary Parent(PdfDocument document, PdfArray fields, string name)
    {
        PdfDictionary? parent = null;
        var siblings = fields;
        foreach (var partial in name.Split('.'))
        {
            var found = siblings.Elements.Select(AsDictionary).OfType<PdfDictionary>()
                .FirstOrDefault(d => d.Elements.GetString("/T") == partial && AsArray(d.Elements["/Kids"]) is not null);
            if (found is null)
            {
                found = new PdfDictionary(document);
                document.Internals.AddObject(found);
                found.Elements["/T"] = Text(partial);
                found.Elements["/Kids"] = new PdfArray(document);
                if (parent is not null)
                {
                    found.Elements["/Parent"] = parent.Reference!;
                }

                siblings.Elements.Add(found.Reference!);
            }

            parent = found;
            siblings = AsArray(found.Elements["/Kids"])!;
        }

        return parent!;
    }

    /// <summary>A widget annotation on its page, with /MK colours and the on state of a button.</summary>
    private static PdfDictionary NewWidget(PdfDocument document, FormFieldSpec spec)
    {
        var page = document.Pages[spec.Page];
        var widget = new PdfDictionary(document);
        document.Internals.AddObject(widget);
        widget.Elements.SetName("/Type", "/Annot");
        widget.Elements.SetName("/Subtype", "/Widget");
        var rect = new PdfArray(document);
        foreach (var value in new[] { spec.Rect.Left, spec.Rect.Bottom, spec.Rect.Right, spec.Rect.Top })
        {
            rect.Elements.Add(new PdfReal(Math.Round(value, 4)));
        }

        widget.Elements["/Rect"] = rect;
        widget.Elements.SetInteger("/F", Print);
        if (page.Reference is { } pageReference)
        {
            widget.Elements["/P"] = pageReference;
        }

        var look = new PdfDictionary(document);
        look.Elements["/BC"] = Gray(document, 0.5);
        if (spec.Kind != FormFieldKind.PushButton)
        {
            look.Elements["/BG"] = Gray(document, 1);
        }

        var rotation = PageBoxes.Rotation(page);
        if (rotation != 0)
        {
            look.Elements.SetInteger("/R", rotation);
        }

        if (spec.Caption is { } caption)
        {
            look.Elements["/CA"] = Text(caption);
        }
        else if (spec.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioGroup)
        {
            look.Elements["/CA"] = new PdfString(spec.Kind == FormFieldKind.CheckBox ? "4" : "l"); // ZapfDingbats check / dot
        }

        widget.Elements["/MK"] = look;
        if (spec.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioGroup)
        {
            // The on state is read back from /AP /N; it is drawn properly once the field exists.
            var states = new PdfDictionary(document);
            states.Elements["/" + (spec.OnState ?? FormFieldSpec.DefaultOnState)] = PdfNull.Value;
            states.Elements["/" + FormField.Off] = PdfNull.Value;
            var appearance = new PdfDictionary(document);
            appearance.Elements["/N"] = states;
            widget.Elements["/AP"] = appearance;
            widget.Elements.SetName("/AS", "/" + FormField.Off);
        }

        AnnotationWriter.OwnAnnotations(document, page).Elements.Add(widget.Reference!);
        return widget;
    }

    /// <summary>
    /// The keystroke and format actions of a number or date field, as Acrobat writes them (AFNumber_*, AFDate_*Ex):
    /// readers check the typing and show the value formatted.
    /// </summary>
    private static PdfDictionary FormatActions(PdfDocument document, FormTextFormat format)
    {
        var (keystroke, display) = format.Kind == FormFormatKind.Number
            ? (string.Create(CultureInfo.InvariantCulture, $"AFNumber_Keystroke({format.Decimals}, {(int)format.Separators}, 0, 0, \"\", true);"),
               string.Create(CultureInfo.InvariantCulture, $"AFNumber_Format({format.Decimals}, {(int)format.Separators}, 0, 0, \"\", true);"))
            : ($"AFDate_KeystrokeEx(\"{format.DatePattern}\");", $"AFDate_FormatEx(\"{format.DatePattern}\");");
        var actions = new PdfDictionary(document);
        actions.Elements["/K"] = JavaScript(document, keystroke);
        actions.Elements["/F"] = JavaScript(document, display);
        return actions;
    }

    private static PdfDictionary JavaScript(PdfDocument document, string script)
    {
        var action = new PdfDictionary(document);
        action.Elements.SetName("/S", "/JavaScript");
        action.Elements["/JS"] = new PdfString(script);
        return action;
    }

    private static void SetValue(PdfDictionary field, FormFieldKind kind, string? value, IReadOnlyList<string>? selected, IReadOnlyList<FormOption> options)
    {
        switch (kind)
        {
            case FormFieldKind.CheckBox or FormFieldKind.RadioGroup:
                field.Elements.SetName("/V", "/" + (string.IsNullOrEmpty(value) ? FormField.Off : value));
                break;
            case FormFieldKind.ListBox:
                var chosen = selected ?? (value is null ? [] : [value]);
                if (chosen.Count == 1)
                {
                    field.Elements["/V"] = Text(chosen[0]);
                }
                else
                {
                    var array = new PdfArray(field.Owner);
                    foreach (var choice in chosen)
                    {
                        array.Elements.Add(Text(choice));
                    }

                    field.Elements["/V"] = array;
                }

                var indices = new PdfArray(field.Owner);
                foreach (var index in chosen.Select(c => options.ToList().FindIndex(o => o.Export == c)).Where(i => i >= 0).Order())
                {
                    indices.Elements.Add(new PdfInteger(index));
                }

                field.Elements["/I"] = indices;
                break;
            case FormFieldKind.PushButton:
                break;
            default:
                if (value is null)
                {
                    field.Elements.Remove("/V");
                }
                else
                {
                    field.Elements["/V"] = Text(value);
                }

                break;
        }
    }

    /// <summary>Redraws every widget of the named fields for their current values.</summary>
    private static void RefreshAll(
        PdfDocument document,
        IReadOnlySet<string> names,
        IReadOnlyDictionary<string, string> captions,
        IReadOnlyDictionary<string, string>? families = null)
    {
        foreach (var (field, _, widgets) in FormReader.Fields(document).Where(f => names.Contains(f.Field.Name)))
        {
            var shown = field.Kind == FormFieldKind.PushButton && captions.TryGetValue(field.Name, out var caption) ? field with { Value = caption } : field;
            if (families is not null && families.TryGetValue(field.Name, out var family))
            {
                shown = shown with { FontFamily = family };
            }

            for (var i = 0; i < widgets.Count; i++)
            {
                var widget = widgets[i];
                var page = field.Widgets[i].Page is var index and >= 0 ? document.Pages[index] : null;
                FormAppearances.Refresh(document, shown, widget, page is null ? 0 : PageBoxes.Rotation(page));
            }
        }
    }

    private static PdfString Text(string text)
        => text.All(c => c < 128) ? new PdfString(text) : new PdfString(text, PdfStringEncoding.Unicode);

    private static PdfArray Gray(PdfDocument document, double level)
    {
        var array = new PdfArray(document);
        array.Elements.Add(new PdfReal(level));
        return array;
    }
}

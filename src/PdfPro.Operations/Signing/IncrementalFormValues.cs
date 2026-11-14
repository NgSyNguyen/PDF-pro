using PdfPro.Core.Forms;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Signing;

/// <summary>
/// Form values filled in a session, appended to a signed file as an incremental update: every byte of the file stays as
/// it was, so its digital signatures stay valid (a reader reports the form as filled in after signing). The fields keep
/// their object numbers — only their value (/V, /I) changes, and the widgets of check boxes and radio buttons their
/// state (/AS); text and choice widgets get a new appearance, drawn by <see cref="FormAppearances"/> in a scratch
/// document (saved, so that PDFsharp embeds its fonts) and carried over with new object numbers.
/// </summary>
public static class IncrementalFormValues
{
    private const string WidgetKey = "/PdfProWidget";

    /// <summary>The file with the fields given <paramref name="values"/>.</summary>
    /// <exception cref="InvalidDataException">
    /// The file cannot be updated this way: encrypted or unreadable, a field it does not have or that cannot be filled,
    /// a field a signature locks, or a field or widget written inside another object.
    /// </exception>
    public static byte[] Append(byte[] file, IReadOnlyCollection<FormValue> values)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return file;
        }

        var trailer = IncrementalUpdate.ReadTrailer(file);
        if (trailer.IsEncrypted)
        {
            throw new InvalidDataException("An encrypted file cannot be updated this way.");
        }

        PdfDocument original;
        try
        {
            original = PdfSafeReader.Open(new MemoryStream(file, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("The file cannot be read.", ex);
        }

        using (original)
        {
            var fields = FormReader.Fields(original).ToDictionary(f => f.Field.Name, StringComparer.Ordinal);
            var locked = LockedBySignatures(fields.Values.Select(f => (f.Field, f.Dictionary)));
            foreach (var value in values)
            {
                if (!fields.TryGetValue(value.Name, out var field) || !field.Field.IsFillable)
                {
                    throw new InvalidDataException("A field of the values is not in the file, or cannot be filled.");
                }

                if (locked(value.Name))
                {
                    throw new InvalidDataException("A signature locks a field of the values.");
                }

                FormWriter.SetValue(field.Dictionary, field.Field.Kind, TextNormalizer.ToNfc(value.Value), value.Selected?.Select(TextNormalizer.ToNfc).ToList(), field.Field.Options);
            }

            // The fields read again: their models now hold the new values.
            var names = values.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
            var changed = new Dictionary<int, PdfDictionary>();
            var redraw = new List<(PdfDictionary Widget, FormField Field, int Rotation)>();
            foreach (var (field, dictionary, widgets) in FormReader.Fields(original).Where(f => names.Contains(f.Field.Name)))
            {
                Keep(changed, dictionary);
                for (var i = 0; i < widgets.Count; i++)
                {
                    var widget = widgets[i];
                    switch (field.Kind)
                    {
                        case FormFieldKind.CheckBox or FormFieldKind.RadioGroup:
                            var on = FormReader.OnState(widget);
                            widget.Elements.SetName("/AS", "/" + (on is not null && field.Value == on ? on : FormField.Off));
                            Keep(changed, widget);
                            break;
                        case FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox:
                            var page = field.Widgets[i].Page is var index and >= 0 && index < original.PageCount ? original.Pages[index] : null;
                            redraw.Add((widget, field, page is null ? 0 : PageBoxes.Rotation(page)));
                            Keep(changed, widget);
                            break;
                    }
                }
            }

            var next = trailer.Size;
            var objects = new List<UpdatedObject>();
            if (redraw.Count > 0)
            {
                next = Appearances(redraw, next, objects);
            }

            var originalNames = new PdfObjectWriter(r => $"{r.ObjectNumber} {r.GenerationNumber} R");
            foreach (var (number, dictionary) in changed)
            {
                objects.Add(new UpdatedObject(number, dictionary.Reference!.GenerationNumber, originalNames.Body(dictionary)));
            }

            return IncrementalUpdate.Append(file, trailer, objects, out _);
        }
    }

    /// <summary>
    /// Draws the widgets' new appearances in a scratch document, adds their objects (numbered from
    /// <paramref name="next"/>) to <paramref name="objects"/>, and points each widget's /AP at its own. Returns the next
    /// free number.
    /// </summary>
    private static int Appearances(List<(PdfDictionary Widget, FormField Field, int Rotation)> redraw, int next, List<UpdatedObject> objects)
    {
        FontLibrary.Register();
        byte[] saved;
        using (var scratch = new PdfDocument())
        {
            var page = scratch.AddPage();
            var annotations = new PdfArray(scratch);
            for (var i = 0; i < redraw.Count; i++)
            {
                var rect = FormReader.Rect(redraw[i].Widget);
                var widget = new PdfDictionary(scratch);
                widget.Elements.SetName("/Type", "/Annot");
                widget.Elements.SetName("/Subtype", "/Widget");
                widget.Elements["/Rect"] = new PdfArray(scratch, new PdfReal(rect.Left), new PdfReal(rect.Bottom), new PdfReal(rect.Right), new PdfReal(rect.Top));
                widget.Elements[WidgetKey] = new PdfInteger(i);
                scratch.Internals.AddObject(widget);
                FormAppearances.Refresh(scratch, redraw[i].Field, widget, redraw[i].Rotation);
                annotations.Elements.Add(widget.Reference!);
            }

            page.Elements["/Annots"] = annotations;

            // Saved only to be read back (PDFsharp embeds fonts when saving); nothing of it reaches a user's file but
            // the appearances' own objects.
            using var stream = new MemoryStream();
            scratch.Save(stream, closeStream: false);
            saved = stream.ToArray();
        }

        using var read = PdfSafeReader.Open(new MemoryStream(saved, writable: false), PdfDocumentOpenMode.Import);
        var numbers = new Dictionary<int, int>();
        var order = new List<PdfObject>();
        var normals = new Dictionary<int, PdfReference>();
        var widgets = AsArray(read.Pages[0].Elements["/Annots"])?.Elements.Select(Resolve).OfType<PdfDictionary>() ?? [];
        foreach (var widget in widgets)
        {
            if (AsDictionary(widget.Elements["/AP"])?.Elements["/N"] is not PdfReference normal)
            {
                throw new InvalidDataException("A widget's appearance was not drawn.");
            }

            normals[widget.Elements.GetInteger(WidgetKey)] = normal;
            var pending = new Stack<PdfItem>([normal]);
            while (pending.Count > 0)
            {
                switch (pending.Pop())
                {
                    case PdfReference reference when !numbers.ContainsKey(reference.ObjectNumber):
                        numbers[reference.ObjectNumber] = next++;
                        order.Add(reference.Value);
                        pending.Push(reference.Value);
                        break;
                    case PdfDictionary dictionary:
                        foreach (var value in dictionary.Elements.Values.OfType<PdfItem>())
                        {
                            pending.Push(value);
                        }

                        break;
                    case PdfArray array:
                        foreach (var value in array.Elements)
                        {
                            pending.Push(value);
                        }

                        break;
                }
            }
        }

        var names = new PdfObjectWriter(r => $"{numbers[r.ObjectNumber]} 0 R");
        objects.AddRange(order.Select(o => new UpdatedObject(numbers[o.Reference!.ObjectNumber], 0, names.Body(o))));
        for (var i = 0; i < redraw.Count; i++)
        {
            redraw[i].Widget.Elements["/AP"] = new PdfLiteral($"<< /N {numbers[normals[i].ObjectNumber]} 0 R >>");
        }

        return next;
    }

    /// <summary>
    /// Which fields the signed signature fields lock (their /Lock, 12.7.5.5): every field (/All), the fields named
    /// (/Include), or all but them (/Exclude). Filling a locked field would break the signature.
    /// </summary>
    private static Func<string, bool> LockedBySignatures(IEnumerable<(FormField Field, PdfDictionary Dictionary)> fields)
    {
        var all = false;
        var included = new HashSet<string>(StringComparer.Ordinal);
        var excluded = new List<HashSet<string>>();
        foreach (var (field, dictionary) in fields.Where(f => f.Field.Kind == FormFieldKind.Signature && f.Field.Value == FormField.Signed))
        {
            if (AsDictionary(dictionary.Elements["/Lock"]) is not { } lockDictionary)
            {
                continue;
            }

            var named = (AsArray(lockDictionary.Elements["/Fields"])?.Elements.Select(Resolve).OfType<PdfString>().Select(s => s.Value) ?? []).ToHashSet(StringComparer.Ordinal);
            switch (lockDictionary.Elements.GetName("/Action"))
            {
                case "/All":
                    all = true;
                    break;
                case "/Include":
                    included.UnionWith(named);
                    break;
                case "/Exclude":
                    excluded.Add(named);
                    break;
            }
        }

        return name => all || included.Contains(name) || excluded.Any(kept => !kept.Contains(name));
    }

    /// <summary>A changed object of the file, by its number; a field or widget written inside another cannot be updated alone.</summary>
    private static void Keep(Dictionary<int, PdfDictionary> changed, PdfDictionary dictionary)
    {
        if (dictionary.Reference is not { } reference)
        {
            throw new InvalidDataException("A field or widget is written inside another object.");
        }

        changed[reference.ObjectNumber] = dictionary;
    }
}

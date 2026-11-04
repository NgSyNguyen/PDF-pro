using System.Globalization;
using PdfPro.Core.Forms;
using PdfPro.Operations.Forms;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Signing;

/// <summary>What the file shows differently from the revision a signature covers.</summary>
[Flags]
public enum ChangesAfterSigning
{
    None = 0,

    /// <summary>Other signatures were added (the usual way several people sign).</summary>
    Signatures = 1,

    /// <summary>Form fields were filled in, added or removed.</summary>
    FormFields = 2,

    /// <summary>Comments (annotations other than form widgets) were added, changed or removed.</summary>
    Annotations = 4,

    /// <summary>Pages were added, removed, turned, cropped, or what they draw changed.</summary>
    Pages = 8,

    /// <summary>The later revisions could not be compared.</summary>
    Unknown = 16,
}

/// <summary>
/// Compares the revision a signature covers (the file up to the end of its byte range) with the whole file: what a
/// reader sees that the signer did not sign. Objects an incremental update leaves alone keep their bytes, so equal
/// bytes mean unchanged.
/// </summary>
public static class SignedRevisionChanges
{
    /// <summary>What <paramref name="file"/> changed after the revision ending at <paramref name="signedLength"/>.</summary>
    public static ChangesAfterSigning Compare(byte[] file, long signedLength)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (signedLength >= file.Length)
        {
            return ChangesAfterSigning.None;
        }

        if (signedLength <= 0)
        {
            return ChangesAfterSigning.Unknown;
        }

        try
        {
            using var signed = PdfReader.Open(new MemoryStream(file, 0, (int)signedLength, writable: false), PdfDocumentOpenMode.Import);
            using var now = PdfReader.Open(new MemoryStream(file, writable: false), PdfDocumentOpenMode.Import);
            return Fields(signed, now) | Pages(signed, now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A revision PDFsharp cannot read (or one that does not stand alone): what changed cannot be told.
            return ChangesAfterSigning.Unknown;
        }
    }

    private static ChangesAfterSigning Fields(PdfDocument signed, PdfDocument now)
    {
        var before = FormReader.Read(signed);
        var after = FormReader.Read(now);
        var changes = ChangesAfterSigning.None;
        if (after.Count(f => f.IsSigned) > before.Count(f => f.IsSigned))
        {
            changes |= ChangesAfterSigning.Signatures;
        }

        static Dictionary<string, string> Values(IEnumerable<FormField> fields)
            => fields.Where(f => f.Kind != FormFieldKind.Signature)
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => string.Join('\u001F', g.Select(f => $"{f.Kind}|{f.Value}|{string.Join('\u001E', f.Selected)}|{f.Widgets.Count}")), StringComparer.Ordinal);

        var valuesBefore = Values(before);
        var valuesAfter = Values(after);
        if (valuesBefore.Count != valuesAfter.Count || valuesBefore.Any(v => !valuesAfter.TryGetValue(v.Key, out var value) || value != v.Value)
            || before.Count(f => f.Kind == FormFieldKind.Signature) - before.Count(f => f.IsSigned) < after.Count(f => f.Kind == FormFieldKind.Signature) - after.Count(f => f.IsSigned))
        {
            changes |= ChangesAfterSigning.FormFields;
        }

        return changes;
    }

    private static ChangesAfterSigning Pages(PdfDocument signed, PdfDocument now)
    {
        if (signed.PageCount != now.PageCount)
        {
            return ChangesAfterSigning.Pages | Annotations(signed, now);
        }

        var changes = ChangesAfterSigning.None;
        for (var i = 0; i < now.PageCount; i++)
        {
            if (!SameLook(signed.Pages[i], now.Pages[i]))
            {
                changes |= ChangesAfterSigning.Pages;
            }

            if (!Comments(signed.Pages[i]).SequenceEqual(Comments(now.Pages[i]), StringComparer.Ordinal))
            {
                changes |= ChangesAfterSigning.Annotations;
            }
        }

        return changes;
    }

    private static ChangesAfterSigning Annotations(PdfDocument signed, PdfDocument now)
        => signed.Pages.Cast<PdfPage>().SelectMany(Comments).Order(StringComparer.Ordinal).SequenceEqual(now.Pages.Cast<PdfPage>().SelectMany(Comments).Order(StringComparer.Ordinal), StringComparer.Ordinal)
            ? ChangesAfterSigning.None
            : ChangesAfterSigning.Annotations;

    /// <summary>The page's boxes, turn and content stream bytes are those of the signed revision.</summary>
    private static bool SameLook(PdfPage before, PdfPage after)
    {
        foreach (var key in new[] { "/MediaBox", "/CropBox", "/Rotate" })
        {
            if (Text(before.Elements[key]) != Text(after.Elements[key]))
            {
                return false;
            }
        }

        return Content(before).SequenceEqual(Content(after)) && Text(before.Elements["/Resources"]) == Text(after.Elements["/Resources"]);
    }

    /// <summary>The raw bytes of the page's content streams, one after the other.</summary>
    private static byte[] Content(PdfPage page)
    {
        var items = Resolve(page.Elements["/Contents"]) switch
        {
            PdfArray array => array.Elements.Select(Resolve),
            { } single => [single],
            null => [],
        };
        return [.. items.OfType<PdfDictionary>().SelectMany(d => (d.Stream?.Value ?? []).Prepend((byte)0))];
    }

    /// <summary>The page's comments (not form widgets or their pop-ups), each as a short description, in order.</summary>
    private static IEnumerable<string> Comments(PdfPage page)
    {
        if (Resolve(page.Elements["/Annots"]) is not PdfArray annotations)
        {
            yield break;
        }

        foreach (var item in annotations.Elements.Select(Resolve).OfType<PdfDictionary>())
        {
            var subtype = item.Elements.GetName("/Subtype");
            if (subtype is "/Widget" or "/Popup")
            {
                continue;
            }

            yield return string.Join('|', subtype, Text(item.Elements["/Rect"]), Text(item.Elements["/Contents"]), Text(item.Elements["/M"]), Text(item.Elements["/AP"]));
        }
    }

    /// <summary>A comparable text of a value: references by number (unchanged objects keep theirs), values as written.</summary>
    private static string Text(PdfItem? item) => item switch
    {
        null => string.Empty,
        PdfReference reference => reference.ObjectID.ToString(),
        PdfString text => text.Value,
        PdfName name => name.Value,
        PdfInteger number => number.Value.ToString(CultureInfo.InvariantCulture),
        PdfReal real => real.Value.ToString("R", CultureInfo.InvariantCulture),
        PdfArray array => "[" + string.Join(' ', array.Elements.Select(Text)) + "]",
        PdfDictionary dictionary => "<<" + string.Join(' ', dictionary.Elements.Keys.Order(StringComparer.Ordinal).Select(k => k + " " + Text(dictionary.Elements[k]))) + ">>",
        _ => item.ToString() ?? string.Empty,
    };

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
}

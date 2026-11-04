using System.Text;
using PdfPro.Core.Annotations;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Signing;

/// <summary>
/// Annotations added in a session, appended to the file as an incremental update (PROMPTS P6 B9): every byte of the
/// file stays as it was, so its digital signatures stay valid. They are written by <see cref="AnnotationWriter"/> on
/// blank pages of a scratch document (which is saved, so that PDFsharp embeds its fonts) and carried over with new
/// object numbers; the pages of the file get them in their /Annots.
/// </summary>
public static class IncrementalAnnotations
{
    private const string PageKey = "/PdfProPageIndex";

    /// <summary>The file with the annotations appended to its pages (0-based indices).</summary>
    /// <exception cref="InvalidDataException">The file cannot be updated this way (encrypted, unreadable, no such page).</exception>
    /// <exception cref="ArgumentException">An annotation is not valid.</exception>
    public static byte[] Append(byte[] file, IReadOnlyList<(int PageIndex, IReadOnlyList<AnnotationItem> Annotations)> pages)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(pages);
        var trailer = IncrementalUpdate.ReadTrailer(file);
        if (trailer.IsEncrypted)
        {
            throw new InvalidDataException("An encrypted file cannot be updated this way.");
        }

        var changed = pages.Where(p => p.Annotations.Count > 0).ToList();
        if (changed.Count == 0)
        {
            return file;
        }

        PdfDocument original;
        try
        {
            original = PdfReader.Open(new MemoryStream(file, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("The file cannot be read.", ex);
        }

        using (original)
        {
            if (changed.Any(p => p.PageIndex < 0 || p.PageIndex >= original.PageCount) || changed.Select(p => p.PageIndex).Distinct().Count() != changed.Count)
            {
                throw new InvalidDataException("A page of the annotations is not in the file.");
            }

            using var scratch = PdfReader.Open(new MemoryStream(Scratch(changed)), PdfDocumentOpenMode.Import);
            var next = trailer.Size;
            var originalNames = new PdfObjectWriter(r => $"{r.ObjectNumber} {r.GenerationNumber} R");

            // The scratch pages stand for the pages of the file (an annotation's /P).
            var pageOf = new Dictionary<int, string>();
            foreach (var page in scratch.Pages.Cast<PdfPage>())
            {
                var target = original.Pages[page.Elements.GetInteger(PageKey)];
                pageOf[page.Reference!.ObjectNumber] = originalNames.Reference(target.Reference!);
            }

            // Everything the annotations use, each with a number of the update; pages are not followed.
            var numbers = new Dictionary<int, int>();
            var order = new List<PdfObject>();
            var pending = new Stack<PdfItem>(scratch.Pages.Cast<PdfPage>().SelectMany(p => Annotations(p)).Reverse());
            while (pending.Count > 0)
            {
                switch (pending.Pop())
                {
                    case PdfReference reference when !numbers.ContainsKey(reference.ObjectNumber) && !pageOf.ContainsKey(reference.ObjectNumber):
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

            var newNames = new PdfObjectWriter(r => pageOf.TryGetValue(r.ObjectNumber, out var page) ? page : $"{numbers[r.ObjectNumber]} 0 R");
            var objects = order.Select(o => new UpdatedObject(numbers[o.Reference!.ObjectNumber], 0, newNames.Body(o))).ToList();

            // Each page of the file gets its new annotations after the ones it has.
            foreach (var page in scratch.Pages.Cast<PdfPage>())
            {
                var target = original.Pages[page.Elements.GetInteger(PageKey)];
                var list = new StringBuilder("[");
                foreach (var existing in Annotations(target))
                {
                    originalNames.Write(list, existing);
                    list.Append(' ');
                }

                foreach (var added in Annotations(page).OfType<PdfReference>())
                {
                    list.Append(numbers[added.ObjectNumber]).Append(" 0 R ");
                }

                target.Elements["/Annots"] = new PdfLiteral(list.ToString().TrimEnd() + "]");
                objects.Add(new UpdatedObject(target.Reference!.ObjectNumber, target.Reference.GenerationNumber, originalNames.Body(target)));
            }

            return IncrementalUpdate.Append(file, trailer, objects, out _);
        }
    }

    /// <summary>A scratch document with one blank page per changed page, carrying its annotations, saved.</summary>
    private static byte[] Scratch(List<(int PageIndex, IReadOnlyList<AnnotationItem> Annotations)> changed)
    {
        FontLibrary.Register();
        using var scratch = new PdfDocument();
        foreach (var (index, annotations) in changed)
        {
            var page = scratch.AddPage();
            page.Elements[PageKey] = new PdfInteger(index);
            AnnotationWriter.Write(scratch, page, annotations);
        }

        // Saved only to be read back (PDFsharp embeds fonts when saving); nothing of it reaches a user's file but the
        // annotations' own objects.
        using var stream = new MemoryStream();
        scratch.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static IEnumerable<PdfItem> Annotations(PdfPage page)
        => (page.Elements["/Annots"] is PdfReference { Value: PdfArray shared } ? shared : page.Elements["/Annots"] as PdfArray)?.Elements ?? Enumerable.Empty<PdfItem>();
}

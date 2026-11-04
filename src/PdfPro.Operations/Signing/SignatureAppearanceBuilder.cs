using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Stamping;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Signing;

/// <summary>
/// The appearance of a visible signature: the handwritten picture (if any) on the left, the text lines on the right,
/// in Noto Sans so that Vietnamese shows everywhere. It is drawn with PDFsharp in a scratch document, which is saved
/// (PDFsharp embeds its fonts only then) and read back; the form and everything it uses are then given new object
/// numbers for the incremental update.
/// </summary>
internal static class SignatureAppearanceBuilder
{
    private const string Key = "/PdfProAppearance";

    private const double Padding = 2;

    /// <param name="upright">The size of the box as the page is shown.</param>
    /// <param name="rotation">The page's /Rotate: the drawing is turned with it so that it reads upright.</param>
    /// <param name="next">The next free object number; advanced past the objects returned.</param>
    /// <returns>The number of the form and the objects to write.</returns>
    public static (int Form, List<UpdatedObject> Objects) Build(PdfSize upright, int rotation, IReadOnlyList<string> lines, byte[]? image, ref int next)
    {
        byte[] saved;
        using (var scratch = new PdfDocument())
        {
            FontLibrary.Register();
            scratch.AddPage();
            var form = StampForms.Draw(scratch, upright, 0, graphics => Draw(graphics, upright, lines, image));
            if (rotation is 90 or 180 or 270 && AsDictionary(form.Form) is { } dictionary)
            {
                var turn = PdfMatrix.Rotation(rotation);
                dictionary.Elements["/Matrix"] = new PdfArray(scratch, [.. new[] { turn.A, turn.B, turn.C, turn.D, 0, 0 }.Select(v => new PdfReal(Math.Round(v, 4)))]);
            }

            scratch.Internals.Catalog.Elements[Key] = form.Form;
            using var stream = new MemoryStream();
            scratch.Save(stream, closeStream: false);
            saved = stream.ToArray();
        }

        using var read = PdfReader.Open(new MemoryStream(saved), PdfDocumentOpenMode.Import);
        var root = read.Internals.Catalog.Elements[Key] as PdfReference ?? throw new InvalidOperationException("The appearance was not written.");

        // Everything the form uses, each with a number of the update.
        var numbers = new Dictionary<int, int>();
        var order = new List<PdfObject>();
        var pending = new Stack<PdfItem>([root]);
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

        var writer = new PdfObjectWriter(reference => $"{numbers[reference.ObjectNumber]} 0 R");
        var objects = order.Select(o => new UpdatedObject(numbers[o.Reference!.ObjectNumber], 0, writer.Body(o))).ToList();
        return (numbers[root.ObjectNumber], objects);
    }

    /// <summary>The picture left (when there is one), the lines right, as big as they fit.</summary>
    private static void Draw(XGraphics graphics, PdfSize size, IReadOnlyList<string> lines, byte[]? picture)
    {
        var text = new XRect(Padding, Padding, Math.Max(1, size.Width - (2 * Padding)), Math.Max(1, size.Height - (2 * Padding)));
        if (picture is { Length: > 0 })
        {
            var area = lines.Count == 0 ? text : new XRect(text.X, text.Y, text.Width * 0.42, text.Height);
            using var image = XImage.FromStream(new MemoryStream(picture));
            var scale = Math.Min(area.Width / image.PointWidth, area.Height / image.PointHeight);
            var (width, height) = (image.PointWidth * scale, image.PointHeight * scale);
            graphics.DrawImage(image, area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height);
            if (lines.Count > 0)
            {
                text = new XRect(area.Right + Padding, text.Y, Math.Max(1, text.Width - area.Width - Padding), text.Height);
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        var shown = lines.Select(TextNormalizer.ToNfc).ToList();
        var fontSize = Math.Min(10, text.Height / (shown.Count * 1.25));
        var widest = shown.Max(line => graphics.MeasureString(line, new XFont(FontFamilies.NotoSans, fontSize)).Width);
        if (widest > text.Width)
        {
            fontSize *= text.Width / widest;
        }

        fontSize = Math.Max(fontSize, 2);
        var font = new XFont(FontFamilies.NotoSans, fontSize);
        var lineHeight = fontSize * 1.25;
        var top = text.Y + Math.Max(0, (text.Height - (lineHeight * shown.Count)) / 2);
        for (var i = 0; i < shown.Count; i++)
        {
            graphics.DrawString(shown[i], font, XBrushes.Black, new XRect(text.X, top + (i * lineHeight), text.Width, lineHeight), XStringFormats.CenterLeft);
        }
    }
}

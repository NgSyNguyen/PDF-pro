using PDFiumCore;
using PdfPro.Core.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;

namespace PdfPro.Pdfium;

/// <summary>Fonts for rewriting text objects (edit.content).</summary>
/// <param name="CanWrite">(page index, font base name, text): the object's own font has every glyph of the text.</param>
/// <param name="Fallback">(serif, bold, italic): a bundled Unicode TrueType font program to write with otherwise.</param>
public sealed record ContentEditFonts(Func<int, string, string, bool> CanWrite, Func<bool, bool, bool, byte[]> Fallback);

/// <summary>Changing the drawings of pages (edit.content) through PDFium's page object API.</summary>
public sealed partial class PdfiumDocument
{
    /// <summary>Line spacing of wrapped text, in font sizes.</summary>
    private const float LineSpacing = 1.2f;

    /// <summary>TrueType font programs for FPDFText_LoadFont.</summary>
    private const int TrueTypeFont = 2;

    /// <summary>
    /// Applies the edits (all objects are found before anything changes) and regenerates the content of the changed
    /// pages. Returns, for each edit, whether its text was rewritten with a bundled font.
    /// </summary>
    /// <exception cref="InvalidDataException">An object is not on its page any more.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not rewrite a page.</exception>
    public IReadOnlyList<bool> ApplyContentEdits(IReadOnlyList<ContentEdit> edits, ContentEditFonts fonts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(fonts);
        var native = Native;
        var replaced = new bool[edits.Count];
        foreach (var page in edits.Select((Edit, Order) => (Edit, Order)).GroupBy(e => e.Edit.Target.Page))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageIndex = page.Key - 1;
            CheckPageIndex(pageIndex);
            var loaded = GetCachedPage(pageIndex);
            var pageNative = loaded.Native;
            var targets = page.Select(e => (e.Edit, e.Order, Object: Find(pageNative, e.Edit.Target))).ToList();
            foreach (var (edit, order, item) in targets)
            {
                switch (edit)
                {
                    case ReplaceTextEdit text:
                        replaced[order] = ReplaceText(native, pageIndex, pageNative, item, text, fonts);
                        break;
                    case DeleteObjectEdit:
                        Remove(pageNative, item);
                        break;
                    case MoveObjectEdit move:
                        fpdf_edit.FPDFPageObjTransform(item, 1, 0, 0, 1, move.Dx, move.Dy);
                        break;
                    default:
                        throw new NotSupportedException($"Unknown content edit {edit.GetType().Name}.");
                }
            }

            if (fpdf_edit.FPDFPageGenerateContent(pageNative) == 0)
            {
                throw new InvalidOperationException($"PDFium could not rewrite the content of page {page.Key}.");
            }
        }

        return replaced;
    }

    /// <summary>The object an edit is about: at its index when it is still the one read, otherwise the only one like it.</summary>
    private static FpdfPageobjectT Find(FpdfPageT page, PageObjectRef target)
    {
        var count = fpdf_edit.FPDFPageCountObjects(page);
        if (target.Index < count && Matches(fpdf_edit.FPDFPageGetObject(page, target.Index), target))
        {
            return fpdf_edit.FPDFPageGetObject(page, target.Index);
        }

        FpdfPageobjectT? found = null;
        for (var i = 0; i < count; i++)
        {
            var item = fpdf_edit.FPDFPageGetObject(page, i);
            if (Matches(item, target))
            {
                if (found is not null)
                {
                    throw new InvalidDataException($"Object {target.Index} of page {target.Page} cannot be told from another one any more.");
                }

                found = item;
            }
        }

        return found ?? throw new InvalidDataException($"Object {target.Index} of page {target.Page} is not on the page any more.");
    }

    private static bool Matches(FpdfPageobjectT? item, PageObjectRef target)
    {
        if (item is null || item.__Instance == IntPtr.Zero)
        {
            return false;
        }

        float left = 0, bottom = 0, right = 0, top = 0;
        return fpdf_edit.FPDFPageObjGetBounds(item, ref left, ref bottom, ref right, ref top) != 0
            && target.Matches((PageObjectKind)fpdf_edit.FPDFPageObjGetType(item), new PdfRect(left, bottom, right, top));
    }

    /// <summary>
    /// Writes new words into a text object: in place when its font has every glyph and one line is enough; otherwise as
    /// new text objects (one per line) with the same transform, size and colours, the old one removed. True when a
    /// bundled font was used.
    /// </summary>
    private static bool ReplaceText(FpdfDocumentT document, int pageIndex, FpdfPageT page, FpdfPageobjectT item, ReplaceTextEdit edit, ContentEditFonts fonts)
    {
        using var textPage = new TextPageScope(page);
        var info = PdfiumPage.ReadTextObject(item, textPage.Text);
        var text = TextNormalizer.ToNfc(edit.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
        var font = fpdf_edit.FPDFTextObjGetFont(item);
        float size = 0;
        _ = fpdf_edit.FPDFTextObjGetFontSize(item, ref size);
        var keep = font is not null && font.__Instance != IntPtr.Zero && fonts.CanWrite(pageIndex, info.FontName, text.Replace("\n", string.Empty, StringComparison.Ordinal));
        FpdfFontT? loaded = null;
        try
        {
            var writer = font!;
            if (!keep)
            {
                loaded = LoadFont(document, fonts.Fallback(info.IsSerif, info.IsBold, info.IsItalic));
                writer = loaded;
            }

            var width = Width(keep ? writer : font, info.Text, size);
            var lines = Lines(text, edit.Overflow == TextOverflow.Wrap && width > 0 ? width : float.PositiveInfinity, writer, size);
            if (keep && lines.Count == 1)
            {
                SetText(item, lines[0]);
                return false;
            }

            var matrix = PdfiumPage.Matrix(item);
            var index = IndexOf(page, item);
            uint red = 0, green = 0, blue = 0, alpha = 255;
            var hasFill = fpdf_edit.FPDFPageObjGetFillColor(item, ref red, ref green, ref blue, ref alpha) != 0;
            uint strokeRed = 0, strokeGreen = 0, strokeBlue = 0, strokeAlpha = 255;
            var hasStroke = fpdf_edit.FPDFPageObjGetStrokeColor(item, ref strokeRed, ref strokeGreen, ref strokeBlue, ref strokeAlpha) != 0;
            var mode = fpdf_edit.FPDFTextObjGetTextRenderMode(item);
            for (var line = 0; line < lines.Count; line++)
            {
                var created = fpdf_edit.FPDFPageObjCreateTextObj(document, writer, size);
                if (created is null || created.__Instance == IntPtr.Zero)
                {
                    throw new InvalidOperationException("PDFium could not create a text object.");
                }

                SetText(created, lines[line]);
                if (hasFill)
                {
                    fpdf_edit.FPDFPageObjSetFillColor(created, red, green, blue, alpha);
                }

                if (hasStroke)
                {
                    fpdf_edit.FPDFPageObjSetStrokeColor(created, strokeRed, strokeGreen, strokeBlue, strokeAlpha);
                }

                if (mode != FPDF_TEXT_RENDERMODE.FPDF_TEXTRENDERMODE_UNKNOWN)
                {
                    fpdf_edit.FPDFTextObjSetTextRenderMode(created, mode);
                }

                // Each line one line height further down the text's own vertical axis.
                var down = line * size * LineSpacing;
                using var placed = new FS_MATRIX_
                {
                    A = (float)matrix.A,
                    B = (float)matrix.B,
                    C = (float)matrix.C,
                    D = (float)matrix.D,
                    E = (float)(matrix.E - (matrix.C * down)),
                    F = (float)(matrix.F - (matrix.D * down)),
                };
                fpdf_edit.FPDFPageObjSetMatrix(created, placed);
                fpdf_edit.FPDFPageInsertObjectAtIndex(page, created, (ulong)(index + 1 + line));
            }

            Remove(page, item);
            return !keep;
        }
        finally
        {
            if (loaded is not null)
            {
                fpdf_edit.FPDFFontClose(loaded);
            }
        }
    }

    private static unsafe FpdfFontT LoadFont(FpdfDocumentT document, byte[] program)
    {
        fixed (byte* data = program)
        {
            var font = fpdf_edit.FPDFTextLoadFont(document, data, (uint)program.Length, TrueTypeFont, 1);
            return font is null || font.__Instance == IntPtr.Zero
                ? throw new InvalidOperationException("PDFium could not load the bundled font.")
                : font;
        }
    }

    private static void SetText(FpdfPageobjectT item, string text)
    {
        var chars = new ushort[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
        {
            chars[i] = text[i];
        }

        if (fpdf_edit.FPDFTextSetText(item, ref chars[0]) == 0)
        {
            throw new InvalidOperationException("PDFium could not write the text.");
        }
    }

    private static void Remove(FpdfPageT page, FpdfPageobjectT item)
    {
        if (fpdf_edit.FPDFPageRemoveObject(page, item) == 0)
        {
            throw new InvalidOperationException("PDFium could not remove an object from the page.");
        }

        fpdf_edit.FPDFPageObjDestroy(item);
    }

    private static int IndexOf(FpdfPageT page, FpdfPageobjectT item)
    {
        var count = fpdf_edit.FPDFPageCountObjects(page);
        for (var i = 0; i < count; i++)
        {
            if (fpdf_edit.FPDFPageGetObject(page, i)?.__Instance == item.__Instance)
            {
                return i;
            }
        }

        return count - 1;
    }

    /// <summary>The advance of <paramref name="text"/> in text space units (font size included).</summary>
    private static float Width(FpdfFontT? font, string text, float size)
    {
        if (font is null || font.__Instance == IntPtr.Zero)
        {
            return 0;
        }

        var total = 0f;
        foreach (var rune in text.EnumerateRunes())
        {
            var width = 0f;
            if (fpdf_edit.FPDFFontGetGlyphWidth(font, (uint)rune.Value, size, ref width) != 0)
            {
                total += width;
            }
        }

        return total;
    }

    /// <summary>The text broken into lines no wider than <paramref name="width"/> (at spaces; a word too long stays whole), and at line feeds.</summary>
    private static List<string> Lines(string text, float width, FpdfFontT font, float size)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (float.IsPositiveInfinity(width))
            {
                lines.Add(paragraph);
                continue;
            }

            var line = string.Empty;
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Width(font, candidate, size) > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            lines.Add(line);
        }

        return lines;
    }

    /// <summary>A text page loaded for reading one object's characters, closed afterwards.</summary>
    private readonly struct TextPageScope : IDisposable
    {
        public TextPageScope(FpdfPageT page)
        {
            Text = fpdf_text.FPDFTextLoadPage(page);
            if (Text is null || Text.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("PDFium could not load the text of the page.");
            }
        }

        public FpdfTextpageT Text { get; }

        public void Dispose() => fpdf_text.FPDFTextClosePage(Text);
    }
}

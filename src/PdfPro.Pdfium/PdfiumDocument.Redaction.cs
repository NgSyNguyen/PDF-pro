using System.Runtime.InteropServices;
using PDFiumCore;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;

namespace PdfPro.Pdfium;

/// <summary>Why a page could not be redacted keeping its text and drawings (it is then redacted as a picture).</summary>
public enum VectorRedactionProblem
{
    None,

    /// <summary>Content of a form XObject (a drawing pages may share) lies under an area.</summary>
    SharedDrawing,

    /// <summary>Text under an area cannot be told apart character by character, or does not run left to right.</summary>
    UnreadableText,

    /// <summary>A picture under an area cannot be decoded.</summary>
    UnreadableImage,

    /// <summary>An object of an unknown kind lies under an area.</summary>
    UnknownObject,

    /// <summary>PDFium could not rewrite the page.</summary>
    RewriteFailed,

    /// <summary>After rewriting, text is left under an area, or text outside the areas moved or changed.</summary>
    TextCheckFailed,

    /// <summary>After rewriting, the page outside the areas does not look as it did.</summary>
    LooksDifferent,
}

/// <summary>What became of a page redacted keeping its text and drawings.</summary>
/// <param name="Problem">None when the page was redacted; otherwise why not (the page must then be redacted another way).</param>
/// <param name="RemovedCharacters">Characters taken out of the page's text.</param>
public sealed record VectorRedactionResult(VectorRedactionProblem Problem, int RemovedCharacters = 0)
{
    public bool Done => Problem == VectorRedactionProblem.None;
}

/// <summary>
/// Redaction that keeps a page's text and drawings (security.redact, vector mode) through PDFium's page object API: only
/// what lies under the areas goes — characters, the parts of lines and filled rectangles, the pixels of pictures — and
/// boxes are drawn in their place. The page is then read again and checked: no character under an area, every other
/// character where it was, the page looking as before outside the areas. A page that fails is reported, not fixed:
/// its content is left half-changed and must be replaced (the caller redacts it as a picture).
/// </summary>
public sealed partial class PdfiumDocument
{
    /// <summary>A character counts as under an area when it overlaps it by more than this (points) both ways.</summary>
    private const double MinCharOverlap = 0.1;

    /// <summary>How far (points) a character left in place may have moved after the page is rewritten.</summary>
    private const double OriginTolerance = 0.3;

    /// <summary>Resolution of the before/after comparison (dots per inch / 72).</summary>
    private const double CheckScale = 100.0 / 72;

    /// <summary>Pixels within this distance of what was removed are not compared (anti-aliased edges).</summary>
    private const int CheckMargin = 2;

    /// <summary>Colour difference (0–255, any channel) for a pixel to count as changed.</summary>
    private const int PixelTolerance = 48;

    private const int MaxFormDepth = 8;

    /// <summary>
    /// Redacts one page keeping its text and drawings: removes what lies under <paramref name="rects"/> (PDF user space),
    /// draws boxes in <paramref name="fill"/>, drops the marked-content properties (/ActualText, /Alt…) for which
    /// <paramref name="namesRemoved"/> is true, rewrites the page and checks it. On failure the page's content may be
    /// half-changed: the caller must replace it.
    /// </summary>
    public VectorRedactionResult RedactPageKeepingVectors(int pageIndex, IReadOnlyList<PdfRect> rects, RgbColor fill, Func<string, bool> namesRemoved)
    {
        ArgumentNullException.ThrowIfNull(rects);
        ArgumentNullException.ThrowIfNull(namesRemoved);
        var document = Native;
        CheckPageIndex(pageIndex);
        var before = CheckImage(pageIndex);
        var page = GetCachedPage(pageIndex).Native;
        var objects = TopLevelObjects(page);
        var index = new Dictionary<IntPtr, int>();
        for (var i = 0; i < objects.Count; i++)
        {
            index[objects[i].__Instance] = i;
        }

        // Marked text naming what is removed goes first: PDFium reads /ActualText in place of the characters it covers.
        DropMarkedText(page, namesRemoved);
        List<PageChar> chars;
        using (var text = new TextPageScope(page))
        {
            chars = ReadChars(text.Text, index);
        }

        var under = chars.Select(c => Under(c, rects)).ToArray();
        if (chars.Where((c, i) => under[i] && c.Object < 0).Any())
        {
            return new VectorRedactionResult(VectorRedactionProblem.SharedDrawing);
        }

        // Everything is decided before anything changes.
        var excluded = new List<PdfRect>(rects);
        var coarse = new List<PdfRect>();
        var actions = new List<(FpdfPageobjectT Item, Action Apply)>();
        var removed = 0;
        for (var i = 0; i < objects.Count; i++)
        {
            var item = objects[i];
            var bounds = Bounds(item);
            var hits = bounds is { } box && rects.Any(r => RectClipping.Overlaps(box, r));
            switch (fpdf_edit.FPDFPageObjGetType(item))
            {
                case 1: // text
                    var mine = Enumerable.Range(0, chars.Count).Where(c => chars[c].Object == i).ToList();
                    if (mine.Count == 0)
                    {
                        if (!hits)
                        {
                            break;
                        }

                        if (!rects.Any(r => Contains(r, bounds!.Value)))
                        {
                            return new VectorRedactionResult(VectorRedactionProblem.UnreadableText);
                        }

                        actions.Add((item, () => Remove(page, item)));
                        break;
                    }

                    if (!mine.Any(c => under[c]))
                    {
                        break;
                    }

                    if (TextRuns(item, [.. mine.Select(c => (chars[c], under[c]))]) is not { } runs)
                    {
                        return new VectorRedactionResult(VectorRedactionProblem.UnreadableText);
                    }

                    foreach (var c in mine.Where(c => under[c]))
                    {
                        removed++;
                        excluded.Add(chars[c].Loose.IsEmpty ? chars[c].Box : chars[c].Loose);
                    }

                    actions.Add((item, () => SplitText(document, page, item, runs)));
                    break;
                case 2: // path
                    if (hits)
                    {
                        actions.Add((item, () => RedactPath(page, item, rects, excluded)));
                    }

                    break;
                case 3: // image
                    if (hits)
                    {
                        if (!CanReadPixels(item))
                        {
                            return new VectorRedactionResult(VectorRedactionProblem.UnreadableImage);
                        }

                        actions.Add((item, () => RedactImage(document, page, item, rects, fill, excluded, coarse)));
                    }

                    break;
                case 4: // shading: a colour gradient, nothing to read in it
                    break;
                case 5: // form XObject
                    if (hits && FormDrawsUnder(item, PdfiumPage.Matrix(item), rects, 0))
                    {
                        return new VectorRedactionResult(VectorRedactionProblem.SharedDrawing);
                    }

                    break;
                default:
                    if (hits)
                    {
                        return new VectorRedactionResult(VectorRedactionProblem.UnknownObject);
                    }

                    break;
            }
        }

        try
        {
            foreach (var (_, apply) in actions)
            {
                apply();
            }

            DrawBoxes(page, rects, fill);
        }
        catch (InvalidOperationException)
        {
            return new VectorRedactionResult(VectorRedactionProblem.RewriteFailed);
        }

        if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
        {
            return new VectorRedactionResult(VectorRedactionProblem.RewriteFailed);
        }

        // The page as written: parsed again from its new content.
        ForgetCachedPage(pageIndex);
        var written = GetCachedPage(pageIndex).Native;
        List<PageChar> after;
        using (var text = new TextPageScope(written))
        {
            after = ReadChars(text.Text, objects: null);
        }

        if (after.Any(c => Under(c, rects)) || !SameChars([.. chars.Where((_, i) => !under[i])], after))
        {
            return new VectorRedactionResult(VectorRedactionProblem.TextCheckFailed);
        }

        return LooksAlike(pageIndex, before, CheckImage(pageIndex), excluded, coarse)
            ? new VectorRedactionResult(VectorRedactionProblem.None, removed)
            : new VectorRedactionResult(VectorRedactionProblem.LooksDifferent);
    }

    /// <summary>A character of the text layer: its top-level object (-1 inside a form XObject), code, origin and boxes.</summary>
    private readonly record struct PageChar(int Object, uint Code, PdfPoint Origin, PdfRect Box, PdfRect Loose, bool MapError);

    /// <summary>A run of characters kept from a text object: its text, where its first character starts, the advances of the others.</summary>
    private sealed record TextRun(string Text, PdfPoint Origin, float[] Positions);

    private static List<FpdfPageobjectT> TopLevelObjects(FpdfPageT page)
    {
        var count = fpdf_edit.FPDFPageCountObjects(page);
        var objects = new List<FpdfPageobjectT>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            if (fpdf_edit.FPDFPageGetObject(page, i) is { } item && item.__Instance != IntPtr.Zero)
            {
                objects.Add(item);
            }
        }

        return objects;
    }

    /// <summary>The characters PDFium reads from the page (not the ones it makes up between words and lines).</summary>
    private static List<PageChar> ReadChars(FpdfTextpageT text, Dictionary<IntPtr, int>? objects)
    {
        var count = fpdf_text.FPDFTextCountChars(text);
        var chars = new List<PageChar>(Math.Max(0, count));
        using var loose = new FS_RECTF_();
        for (var i = 0; i < count; i++)
        {
            if (fpdf_text.FPDFTextIsGenerated(text, i) == 1)
            {
                continue;
            }

            var owner = fpdf_text.FPDFTextGetTextObject(text, i);
            var objectIndex = objects is null || owner is null || !objects.TryGetValue(owner.__Instance, out var found) ? -1 : found;
            double x = 0, y = 0, left = 0, right = 0, bottom = 0, top = 0;
            _ = fpdf_text.FPDFTextGetCharOrigin(text, i, ref x, ref y);
            var box = fpdf_text.FPDFTextGetCharBox(text, i, ref left, ref right, ref bottom, ref top) != 0 ? new PdfRect(left, bottom, right, top) : default;
            var looseBox = fpdf_text.FPDFTextGetLooseCharBox(text, i, loose) != 0 ? new PdfRect(loose.Left, loose.Bottom, loose.Right, loose.Top) : default;
            chars.Add(new PageChar(objectIndex, fpdf_text.FPDFTextGetUnicode(text, i), new PdfPoint(x, y), box, looseBox, fpdf_text.FPDFTextHasUnicodeMapError(text, i) == 1));
        }

        return chars;
    }

    /// <summary>
    /// Whether a character is under an area: its glyph overlaps one; a character without ink (a space) by the centre of
    /// its line-high box.
    /// </summary>
    private static bool Under(PageChar character, IReadOnlyList<PdfRect> rects)
    {
        if (!character.Box.IsEmpty)
        {
            return rects.Any(r =>
                Math.Min(character.Box.Right, r.Right) - Math.Max(character.Box.Left, r.Left) > MinCharOverlap
                && Math.Min(character.Box.Top, r.Top) - Math.Max(character.Box.Bottom, r.Bottom) > MinCharOverlap);
        }

        var shape = character.Loose.IsEmpty ? new PdfRect(character.Origin.X, character.Origin.Y, character.Origin.X, character.Origin.Y) : character.Loose;
        var centre = new PdfPoint((shape.Left + shape.Right) / 2, (shape.Bottom + shape.Top) / 2);
        return rects.Any(r => centre.X > r.Left && centre.X < r.Right && centre.Y > r.Bottom && centre.Y < r.Top);
    }

    /// <summary>
    /// The runs of characters a text object keeps, each placed where its characters were (advances along the text's own
    /// horizontal axis), or null when that cannot be done exactly: a kept character without a known Unicode value, or
    /// characters that do not lie on one horizontal line of the text.
    /// </summary>
    private static List<TextRun>? TextRuns(FpdfPageobjectT item, IReadOnlyList<(PageChar Char, bool Under)> chars)
    {
        var matrix = PdfiumPage.Matrix(item);
        if (Math.Abs(matrix.Determinant) < 1e-12)
        {
            return null;
        }

        var inverse = matrix.Invert();
        var runs = new List<TextRun>();
        var run = new List<PageChar>();
        foreach (var (character, under) in chars.Append((default, true)))
        {
            if (!under)
            {
                if (character.MapError || character.Code is 0 or 0xFFFD or > 0x10FFFF)
                {
                    return null;
                }

                run.Add(character);
                continue;
            }

            // Spaces at either end draw nothing, and PDFium would not read a leading one back: they are left out.
            var first = run.FindIndex(c => !IsSpace(c.Code));
            var last = run.FindLastIndex(c => !IsSpace(c.Code));
            run = first < 0 ? [] : run[first..(last + 1)];
            if (run.Count == 0)
            {
                continue;
            }

            var text = new System.Text.StringBuilder();
            var positions = new List<float>();
            for (var k = 0; k < run.Count; k++)
            {
                var code = run[k].Code;
                var (dx, dy) = (run[k].Origin.X - run[0].Origin.X, run[k].Origin.Y - run[0].Origin.Y);
                var along = (inverse.A * dx) + (inverse.C * dy);
                var across = (inverse.B * dx) + (inverse.D * dy);
                if (Math.Abs(across) > 0.05)
                {
                    return null; // risen, lowered or vertical text
                }

                // A character outside the basic plane may come as two halves sharing one glyph: one position for both.
                if (code is >= 0xDC00 and <= 0xDFFF && k > 0 && run[k - 1].Code is >= 0xD800 and <= 0xDBFF)
                {
                    text.Append((char)code);
                    continue;
                }

                text.Append(code <= 0xFFFF ? ((char)code).ToString() : char.ConvertFromUtf32((int)code));
                if (k > 0)
                {
                    positions.Add((float)along);
                }
            }

            runs.Add(new TextRun(text.ToString(), run[0].Origin, [.. positions]));
            run = [];
        }

        return runs;
    }

    /// <summary>The text object becomes its kept runs: new objects with its font, size, colours and transform.</summary>
    private static void SplitText(FpdfDocumentT document, FpdfPageT page, FpdfPageobjectT item, IReadOnlyList<TextRun> runs)
    {
        var font = fpdf_edit.FPDFTextObjGetFont(item);
        float size = 0;
        if (font is null || font.__Instance == IntPtr.Zero || fpdf_edit.FPDFTextObjGetFontSize(item, ref size) == 0)
        {
            throw new InvalidOperationException("The text object has no font.");
        }

        var matrix = PdfiumPage.Matrix(item);
        uint red = 0, green = 0, blue = 0, alpha = 255;
        var hasFill = fpdf_edit.FPDFPageObjGetFillColor(item, ref red, ref green, ref blue, ref alpha) != 0;
        uint strokeRed = 0, strokeGreen = 0, strokeBlue = 0, strokeAlpha = 255;
        var hasStroke = fpdf_edit.FPDFPageObjGetStrokeColor(item, ref strokeRed, ref strokeGreen, ref strokeBlue, ref strokeAlpha) != 0;
        float strokeWidth = 0;
        var hasStrokeWidth = fpdf_edit.FPDFPageObjGetStrokeWidth(item, ref strokeWidth) != 0;
        var mode = fpdf_edit.FPDFTextObjGetTextRenderMode(item);
        var at = IndexOf(page, item) + 1;
        foreach (var run in runs)
        {
            var created = fpdf_edit.FPDFPageObjCreateTextObj(document, font, size);
            if (created is null || created.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("PDFium could not create a text object.");
            }

            try
            {
                SetText(created, run.Text);
                if (run.Positions.Length > 0 && fpdf_edit.FPDFTextSetPositions(created, ref run.Positions[0], (ulong)run.Positions.Length) == 0)
                {
                    throw new InvalidOperationException("PDFium could not place the characters.");
                }

                if (hasFill)
                {
                    fpdf_edit.FPDFPageObjSetFillColor(created, red, green, blue, alpha);
                }

                if (hasStroke)
                {
                    fpdf_edit.FPDFPageObjSetStrokeColor(created, strokeRed, strokeGreen, strokeBlue, strokeAlpha);
                }

                if (hasStrokeWidth)
                {
                    fpdf_edit.FPDFPageObjSetStrokeWidth(created, strokeWidth);
                }

                if (mode != FPDF_TEXT_RENDERMODE.FPDF_TEXTRENDERMODE_UNKNOWN)
                {
                    fpdf_edit.FPDFTextObjSetTextRenderMode(created, mode);
                }

                using var placed = new FS_MATRIX_
                {
                    A = (float)matrix.A,
                    B = (float)matrix.B,
                    C = (float)matrix.C,
                    D = (float)matrix.D,
                    E = (float)run.Origin.X,
                    F = (float)run.Origin.Y,
                };
                fpdf_edit.FPDFPageObjSetMatrix(created, placed);
            }
            catch
            {
                fpdf_edit.FPDFPageObjDestroy(created);
                throw;
            }

            if (fpdf_edit.FPDFPageInsertObjectAtIndex(page, created, (ulong)at++) == 0)
            {
                fpdf_edit.FPDFPageObjDestroy(created);
                throw new InvalidOperationException("PDFium could not add a text object.");
            }
        }

        Remove(page, item);
    }

    /// <summary>Whether a form XObject draws a line, shape or picture under an area (its text is checked by character).</summary>
    private static bool FormDrawsUnder(FpdfPageobjectT form, PdfMatrix toPage, IReadOnlyList<PdfRect> rects, int depth)
    {
        if (depth >= MaxFormDepth)
        {
            return true;
        }

        var count = fpdf_edit.FPDFFormObjCountObjects(form);
        for (var i = 0; i < count; i++)
        {
            var item = fpdf_edit.FPDFFormObjGetObject(form, (ulong)i);
            if (item is null || item.__Instance == IntPtr.Zero)
            {
                continue;
            }

            var type = fpdf_edit.FPDFPageObjGetType(item);
            if (type is 1 or 4)
            {
                continue;
            }

            if (type == 5)
            {
                if (FormDrawsUnder(item, PdfiumPage.Matrix(item).Then(toPage), rects, depth + 1))
                {
                    return true;
                }

                continue;
            }

            // Bounds of objects in a form are in the form's space; both readings are tried, whichever PDFium uses.
            if (Bounds(item) is { } bounds && rects.Any(r => RectClipping.Overlaps(toPage.TransformBounds(bounds), r) || RectClipping.Overlaps(bounds, r)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Boxes in the areas, drawn last.</summary>
    private static void DrawBoxes(FpdfPageT page, IReadOnlyList<PdfRect> rects, RgbColor fill)
    {
        const int Winding = 2; // FPDF_FILLMODE_WINDING
        foreach (var rect in rects)
        {
            var box = fpdf_edit.FPDFPageObjCreateNewRect((float)rect.Left, (float)rect.Bottom, (float)rect.Width, (float)rect.Height);
            if (box is null || box.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("PDFium could not create a box.");
            }

            fpdf_edit.FPDFPageObjSetFillColor(box, fill.R, fill.G, fill.B, 255);
            fpdf_edit.FPDFPathSetDrawMode(box, Winding, 0);
            fpdf_edit.FPDFPageInsertObject(page, box);
        }
    }

    /// <summary>
    /// Marked-content properties holding text (/ActualText, /Alt, /E…) that names what was removed are dropped: a reader
    /// would otherwise copy or speak them in place of what is shown.
    /// </summary>
    private static void DropMarkedText(FpdfPageT page, Func<string, bool> namesRemoved)
    {
        const int StringValue = 3; // FPDF_OBJECT_STRING
        foreach (var item in TopLevelObjects(page))
        {
            var marks = fpdf_edit.FPDFPageObjCountMarks(item);
            for (var m = 0; m < marks; m++)
            {
                var mark = fpdf_edit.FPDFPageObjGetMark(item, (ulong)m);
                if (mark is null || mark.__Instance == IntPtr.Zero)
                {
                    continue;
                }

                var keys = new List<string>();
                var count = fpdf_edit.FPDFPageObjMarkCountParams(mark);
                for (var p = 0; p < count; p++)
                {
                    var index = (ulong)p;
                    if (ReadUtf16((ref ushort buffer, ulong length, ref ulong needed) => fpdf_edit.FPDFPageObjMarkGetParamKey(mark, index, ref buffer, length, ref needed)) is { Length: > 0 } key)
                    {
                        keys.Add(key);
                    }
                }

                foreach (var key in keys)
                {
                    if (fpdf_edit.FPDFPageObjMarkGetParamValueType(mark, key) == StringValue
                        && ReadUtf16((ref ushort buffer, ulong length, ref ulong needed) => fpdf_edit.FPDFPageObjMarkGetParamStringValue(mark, key, ref buffer, length, ref needed)) is { } value
                        && namesRemoved(value))
                    {
                        _ = fpdf_edit.FPDFPageObjMarkRemoveParam(item, mark, key);
                    }
                }
            }
        }
    }

    private delegate int Utf16Reader(ref ushort buffer, ulong length, ref ulong needed);

    /// <summary>A UTF-16LE string from a PDFium call that reports the bytes it needs, or null.</summary>
    private static string? ReadUtf16(Utf16Reader read)
    {
        ushort probe = 0;
        ulong needed = 0;
        if (read(ref probe, 0, ref needed) == 0 || needed < 2 || needed > 1 << 20)
        {
            return null;
        }

        var buffer = new ushort[(needed + 1) / 2];
        if (read(ref buffer[0], (ulong)buffer.Length * 2, ref needed) == 0)
        {
            return null;
        }

        var length = Array.IndexOf(buffer, (ushort)0);
        return new string([.. buffer.Take(length < 0 ? buffer.Length : length).Select(u => (char)u)]);
    }

    private static bool IsSpace(uint code) => code <= 0xFFFF && char.IsWhiteSpace((char)code);

    /// <summary>
    /// Whether every character left is where it was (same value, same origin), and no other appeared. Spaces are not
    /// compared: they draw nothing, and PDFium makes them up between words anyway.
    /// </summary>
    private static bool SameChars(IReadOnlyList<PageChar> expectedChars, IReadOnlyList<PageChar> actualChars)
    {
        var expected = expectedChars.Where(c => !IsSpace(c.Code)).ToList();
        var actual = actualChars.Where(c => !IsSpace(c.Code)).ToList();
        if (expected.Count != actual.Count)
        {
            return false;
        }

        var pool = expected.GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.Select(c => c.Origin).ToList());
        foreach (var character in actual)
        {
            if (!pool.TryGetValue(character.Code, out var origins))
            {
                return false;
            }

            var match = origins.FindIndex(o => Math.Abs(o.X - character.Origin.X) <= OriginTolerance && Math.Abs(o.Y - character.Origin.Y) <= OriginTolerance);
            if (match < 0)
            {
                return false;
            }

            origins.RemoveAt(match);
        }

        return true;
    }

    private static PdfRect? Bounds(FpdfPageobjectT item)
    {
        float left = 0, bottom = 0, right = 0, top = 0;
        return fpdf_edit.FPDFPageObjGetBounds(item, ref left, ref bottom, ref right, ref top) != 0 ? new PdfRect(left, bottom, right, top) : null;
    }

    private static bool Contains(PdfRect outer, PdfRect inner)
        => inner.Left >= outer.Left && inner.Right <= outer.Right && inner.Bottom >= outer.Bottom && inner.Top <= outer.Top;

    /// <summary>The page rendered for the before/after comparison (no annotations), as BGRx bytes.</summary>
    private (byte[] Pixels, int Width, int Height, int Stride) CheckImage(int pageIndex)
    {
        using var bitmap = RenderPage(pageIndex, new PageRenderOptions { Scale = CheckScale, Flags = PdfRenderFlags.None });
        return (bitmap.Pixels.ToArray(), bitmap.Width, bitmap.Height, bitmap.Stride);
    }

    /// <summary>
    /// Whether the page outside what was removed looks as it did: a handful of pixels may differ (anti-aliasing of
    /// rewritten numbers), no more. Rewritten pictures are compared by the mean of 8×8 blocks: PDFium may smooth the old
    /// picture and not the new one (/Interpolate is not carried over), but a lost transparency or a wrong colour shows.
    /// </summary>
    private bool LooksAlike(
        int pageIndex,
        (byte[] Pixels, int Width, int Height, int Stride) before,
        (byte[] Pixels, int Width, int Height, int Stride) after,
        IReadOnlyList<PdfRect> excluded,
        IReadOnlyList<PdfRect> coarse)
    {
        if (before.Width != after.Width || before.Height != after.Height || before.Stride != after.Stride)
        {
            return false;
        }

        var (width, height, stride) = (before.Width, before.Height, before.Stride);
        var mapper = new PageCoordinateMapper(GetPageGeometry(pageIndex), CheckScale);
        PixelRect Pixels(PdfRect rect, int margin)
        {
            var shown = mapper.PdfRectToScreen(rect);
            var left = Math.Clamp((int)Math.Floor(shown.X) - margin, 0, width);
            var top = Math.Clamp((int)Math.Floor(shown.Y) - margin, 0, height);
            var right = Math.Clamp((int)Math.Ceiling(shown.X + shown.Width) + margin, 0, width);
            var bottom = Math.Clamp((int)Math.Ceiling(shown.Y + shown.Height) + margin, 0, height);
            return new PixelRect(left, top, right - left, bottom - top);
        }

        // 0: compared pixel by pixel; 1: removed, not compared; 2: in a rewritten picture, compared by blocks.
        var kind = new byte[width * height];
        foreach (var region in coarse.Select(r => Pixels(r, 0)))
        {
            for (var y = region.Y; y < region.Bottom; y++)
            {
                Array.Fill(kind, (byte)2, (y * width) + region.X, region.Width);
            }
        }

        foreach (var region in excluded.Select(r => Pixels(r, CheckMargin)))
        {
            for (var y = region.Y; y < region.Bottom; y++)
            {
                Array.Fill(kind, (byte)1, (y * width) + region.X, region.Width);
            }
        }

        if (!BlocksAlike(before, after, kind))
        {
            return false;
        }

        var allowed = Math.Max(12, width * height / 5000);
        var different = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (kind[(y * width) + x] != 0)
                {
                    continue;
                }

                var at = (y * stride) + (x * 4);
                if (Math.Abs(before.Pixels[at] - after.Pixels[at]) > PixelTolerance
                    || Math.Abs(before.Pixels[at + 1] - after.Pixels[at + 1]) > PixelTolerance
                    || Math.Abs(before.Pixels[at + 2] - after.Pixels[at + 2]) > PixelTolerance)
                {
                    if (++different > allowed)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Whether the 8×8 blocks of the rewritten pictures have the same mean colour before and after.</summary>
    private static bool BlocksAlike((byte[] Pixels, int Width, int Height, int Stride) before, (byte[] Pixels, int Width, int Height, int Stride) after, byte[] kind)
    {
        const int Block = 8;
        var (width, height, stride) = (before.Width, before.Height, before.Stride);
        Span<long> sums = stackalloc long[6];
        for (var top = 0; top < height; top += Block)
        {
            for (var left = 0; left < width; left += Block)
            {
                long count = 0;
                sums.Clear();
                for (var y = top; y < Math.Min(top + Block, height); y++)
                {
                    for (var x = left; x < Math.Min(left + Block, width); x++)
                    {
                        if (kind[(y * width) + x] != 2)
                        {
                            continue;
                        }

                        var at = (y * stride) + (x * 4);
                        for (var c = 0; c < 3; c++)
                        {
                            sums[c] += before.Pixels[at + c];
                            sums[c + 3] += after.Pixels[at + c];
                        }

                        count++;
                    }
                }

                for (var c = 0; count > 0 && c < 3; c++)
                {
                    if (Math.Abs(sums[c] - sums[c + 3]) > PixelTolerance * count)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Drops a page from the cache so that the next use parses its content again.</summary>
    private void ForgetCachedPage(int pageIndex)
    {
        for (var node = _pageCache.First; node is not null; node = node.Next)
        {
            if (node.Value.Index == pageIndex)
            {
                _pageCache.Remove(node);
                node.Value.Dispose();
                return;
            }
        }
    }

    private static bool CanReadPixels(FpdfPageobjectT item)
    {
        var bitmap = fpdf_edit.FPDFImageObjGetBitmap(item);
        if (bitmap is null || bitmap.__Instance == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return fpdfview.FPDFBitmapGetFormat(bitmap) is >= 1 and <= 4;
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    /// <summary>
    /// The pixels of a picture under the areas are painted over (every pixel touching an area, so nothing of it shows at
    /// the edges) and the picture is written again as a new image object in the same place (its old stream may be
    /// shown by other pages too, which keep it). A picture inside one area goes altogether.
    /// </summary>
    private static void RedactImage(
        FpdfDocumentT document,
        FpdfPageT page,
        FpdfPageobjectT item,
        IReadOnlyList<PdfRect> rects,
        RgbColor fill,
        List<PdfRect> excluded,
        List<PdfRect> coarse)
    {
        var matrix = PdfiumPage.Matrix(item);
        if ((Bounds(item) is { } bounds && rects.Any(r => Contains(r, bounds))) || Math.Abs(matrix.Determinant) < 1e-12)
        {
            Remove(page, item);
            return;
        }

        var bitmap = fpdf_edit.FPDFImageObjGetBitmap(item);
        if (bitmap is null || bitmap.__Instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("PDFium could not decode a picture.");
        }

        try
        {
            var format = fpdfview.FPDFBitmapGetFormat(bitmap);
            var bytes = format switch { 1 => 1, 2 => 3, 3 or 4 => 4, _ => 0 };
            var (width, height, stride) = (fpdfview.FPDFBitmapGetWidth(bitmap), fpdfview.FPDFBitmapGetHeight(bitmap), fpdfview.FPDFBitmapGetStride(bitmap));
            var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
            if (bytes == 0 || width <= 0 || height <= 0 || buffer == IntPtr.Zero)
            {
                throw new InvalidOperationException("PDFium could not decode a picture.");
            }

            // How far one pixel reaches on the page: an area is grown by that much so that pixels it touches go too.
            var pixel = Math.Max(Math.Sqrt((matrix.A * matrix.A) + (matrix.B * matrix.B)) / width, Math.Sqrt((matrix.C * matrix.C) + (matrix.D * matrix.D)) / height);
            var inverse = matrix.Invert();
            var gray = (byte)Math.Round((0.299 * fill.R) + (0.587 * fill.G) + (0.114 * fill.B));
            var row = new byte[stride];
            var painted = false;
            foreach (var rect in rects)
            {
                var grown = new PdfRect(rect.Left - pixel, rect.Bottom - pixel, rect.Right + pixel, rect.Top + pixel);
                var unit = inverse.TransformBounds(grown);
                var x0 = Math.Clamp((int)Math.Floor(unit.Left * width), 0, width);
                var x1 = Math.Clamp((int)Math.Ceiling(unit.Right * width), 0, width);
                var y0 = Math.Clamp((int)Math.Floor((1 - unit.Top) * height), 0, height);
                var y1 = Math.Clamp((int)Math.Ceiling((1 - unit.Bottom) * height), 0, height);
                for (var y = y0; y < y1; y++)
                {
                    var rowAt = buffer + (y * stride);
                    Marshal.Copy(rowAt, row, 0, stride);
                    var changed = false;
                    for (var x = x0; x < x1; x++)
                    {
                        // Image space: row 0 is the top of the unit square.
                        var centre = matrix.Transform(new PdfPoint((x + 0.5) / width, 1 - ((y + 0.5) / height)));
                        if (!grown.Contains(centre))
                        {
                            continue;
                        }

                        var at = x * bytes;
                        if (bytes == 1)
                        {
                            row[at] = gray;
                        }
                        else
                        {
                            row[at] = fill.B;
                            row[at + 1] = fill.G;
                            row[at + 2] = fill.R;
                            if (bytes == 4)
                            {
                                row[at + 3] = 255;
                            }
                        }

                        changed = true;
                    }

                    if (changed)
                    {
                        Marshal.Copy(row, 0, rowAt, stride);
                        painted = true;
                    }
                }

                excluded.Add(grown);
            }

            if (!painted)
            {
                return;
            }

            coarse.Add(matrix.TransformBounds(new PdfRect(0, 0, 1, 1)));

            var created = fpdf_edit.FPDFPageObjNewImageObj(document);
            if (created is null || created.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("PDFium could not create an image object.");
            }

            using var placed = new FS_MATRIX_
            {
                A = (float)matrix.A,
                B = (float)matrix.B,
                C = (float)matrix.C,
                D = (float)matrix.D,
                E = (float)matrix.E,
                F = (float)matrix.F,
            };
            if (fpdf_edit.FPDFImageObjSetBitmap(null, 0, created, bitmap) == 0
                || fpdf_edit.FPDFPageObjSetMatrix(created, placed) == 0
                || fpdf_edit.FPDFPageInsertObjectAtIndex(page, created, (ulong)(IndexOf(page, item) + 1)) == 0)
            {
                fpdf_edit.FPDFPageObjDestroy(created);
                throw new InvalidOperationException("PDFium could not write a picture.");
            }

            Remove(page, item);
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }
}

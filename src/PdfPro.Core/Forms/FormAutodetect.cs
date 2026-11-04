using System.Text;
using PdfPro.Core.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;

namespace PdfPro.Core.Forms;

/// <summary>A field the page seems to ask for: what it is, where it goes, and the words next to it (its label).</summary>
/// <param name="Kind">Text for blanks and lines, check box for small squares.</param>
/// <param name="Rect">The field's box in the page's PDF space.</param>
/// <param name="Label">The words before a blank or after a box ("Họ và tên"), or empty.</param>
public sealed record FieldSuggestion(FormFieldKind Kind, PdfRect Rect, string Label);

/// <summary>
/// forms.autodetect: proposes fields where a printed form leaves room to write — runs of dots or underscores
/// ("Họ và tên: ............"), lines drawn under empty space, small squares (drawn, or the ☐ character) — each with the
/// words next to it as its label. Places already taken by fields are left out. The user confirms every proposal.
/// </summary>
public static class FormAutodetect
{
    /// <summary>A blank is at least this many dots or underscores.</summary>
    public const int MinLeaders = 4;

    private const double MinRuleWidth = 36;
    private const double MaxRuleThickness = 1.5;
    private const double MinBoxSide = 6;
    private const double MaxBoxSide = 24;
    private const double FieldHeight = 16;
    private const int MaxLabelLength = 40;

    private static readonly string[] BoxCharacters = ["☐", "□", "❑", "❒", "▢"];

    /// <param name="text">The characters of the page.</param>
    /// <param name="objects">The drawings of the page (lines and squares are paths).</param>
    /// <param name="taken">Boxes of the fields already on the page.</param>
    public static IReadOnlyList<FieldSuggestion> Suggest(PageText text, IReadOnlyList<PageObjectInfo> objects, IReadOnlyList<PdfRect> taken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(taken);
        var found = new List<FieldSuggestion>();
        found.AddRange(Blanks(text));
        found.AddRange(BoxCharacterSuggestions(text));
        foreach (var item in objects.Where(o => o.Kind == PageObjectKind.Path))
        {
            var box = item.Bounds;
            if (box.Height <= MaxRuleThickness && box.Width >= MinRuleWidth && !HasTextAbove(text, box))
            {
                var field = new PdfRect(box.Left, box.Top + 0.5, box.Right, box.Top + 0.5 + FieldHeight);
                found.Add(new FieldSuggestion(FormFieldKind.Text, field, LabelBefore(text, field)));
            }
            else if (box.Width is >= MinBoxSide and <= MaxBoxSide && box.Height is >= MinBoxSide and <= MaxBoxSide && Math.Abs(box.Width - box.Height) <= 2)
            {
                found.Add(new FieldSuggestion(FormFieldKind.CheckBox, box, LabelAfter(text, box)));
            }
        }

        // Blanks come first: a line under a run of dots is the same place.
        var result = new List<FieldSuggestion>();
        foreach (var suggestion in found)
        {
            if (!taken.Any(t => Overlaps(t, suggestion.Rect)) && !result.Any(r => Overlaps(r.Rect, suggestion.Rect)))
            {
                result.Add(suggestion);
            }
        }

        return result;
    }

    /// <summary>Runs of dots, ellipses or underscores (spaces between them allowed) on a line.</summary>
    private static IEnumerable<FieldSuggestion> Blanks(PageText text)
    {
        foreach (var line in text.Lines)
        {
            var start = -1;
            var last = -1;
            var leaders = 0;
            var labelStart = line.Start;
            for (var i = line.Start; i <= line.End; i++)
            {
                var c = i < line.End ? text.Chars[i] : null;
                if (c is { IsDrawn: true } && IsLeader(c.Text))
                {
                    start = start < 0 ? i : start;
                    last = i;
                    leaders += c.Text == "…" ? 3 : 1;
                    continue;
                }

                if (c is { IsWhiteSpace: true } && start >= 0)
                {
                    continue;
                }

                if (start >= 0 && leaders >= MinLeaders)
                {
                    var first = text.Chars[start].Box;
                    var end = text.Chars[last].Box;
                    var height = Math.Max(first.Height * 1.1, 12);
                    var rect = new PdfRect(first.Left, first.Bottom, end.Right, first.Bottom + height);
                    yield return new FieldSuggestion(FormFieldKind.Text, rect, Label(text, labelStart, start, fromEnd: true));
                    labelStart = last + 1;
                }

                start = -1;
                last = -1;
                leaders = 0;
            }
        }
    }

    /// <summary>☐ and similar characters standing for a box to tick.</summary>
    private static IEnumerable<FieldSuggestion> BoxCharacterSuggestions(PageText text)
    {
        for (var i = 0; i < text.Count; i++)
        {
            var c = text.Chars[i];
            if (c.IsDrawn && BoxCharacters.Contains(c.Text))
            {
                var side = Math.Min(c.Box.Width, c.Box.Height);
                var box = new PdfRect(c.Box.Left, c.Box.Bottom + ((c.Box.Height - side) / 2), c.Box.Left + side, c.Box.Bottom + ((c.Box.Height + side) / 2));
                // The label runs to the next box of the line (or its end): "☐ Nam ☐ Nữ".
                var end = text.LineAt(i).End;
                var next = Enumerable.Range(i + 1, Math.Max(0, end - i - 1)).FirstOrDefault(j => BoxCharacters.Contains(text.Chars[j].Text), end);
                yield return new FieldSuggestion(FormFieldKind.CheckBox, box, Label(text, i + 1, next, fromEnd: false));
            }
        }
    }

    private static bool IsLeader(string character) => character is "." or "_" or "…" or "·";

    /// <summary>Characters (not leaders) just above a line: it underlines text, it is not a blank.</summary>
    private static bool HasTextAbove(PageText text, PdfRect rule)
        => text.Chars.Any(c => c.IsDrawn && !c.IsWhiteSpace && !IsLeader(c.Text)
            && c.Box.Right > rule.Left + 2 && c.Box.Left < rule.Right - 2 && c.Box.Bottom >= rule.Top - 3 && c.Box.Bottom <= rule.Top + 8);

    /// <summary>The words on the same line just before a box.</summary>
    private static string LabelBefore(PageText text, PdfRect field)
    {
        var chars = text.Chars
            .Select((c, i) => (c, i))
            .Where(x => x.c.IsDrawn && x.c.Box.Right <= field.Left + 2 && x.c.Box.Right >= field.Left - 300 && Middle(x.c.Box) >= field.Bottom - 2 && Middle(x.c.Box) <= field.Top)
            .ToList();
        return chars.Count == 0 ? string.Empty : Clean(string.Concat(chars.OrderBy(x => x.c.Box.Left).Select(x => x.c.Text)), fromEnd: true);
    }

    /// <summary>The words on the same line just after a box.</summary>
    private static string LabelAfter(PageText text, PdfRect box)
    {
        var chars = text.Chars
            .Where(c => c.IsDrawn && c.Box.Left >= box.Right - 1 && c.Box.Left <= box.Right + 200 && Middle(c.Box) >= box.Bottom - 3 && Middle(c.Box) <= box.Top + 3)
            .OrderBy(c => c.Box.Left)
            .ToList();
        return chars.Count == 0 ? string.Empty : Clean(string.Concat(chars.Select(c => c.Text)), fromEnd: false);
    }

    private static double Middle(PdfRect box) => (box.Bottom + box.Top) / 2;

    /// <summary>The text of chars [from, to) as a label; the words nearest the field are kept when it is long.</summary>
    private static string Label(PageText text, int from, int to, bool fromEnd)
    {
        var builder = new StringBuilder();
        for (var i = from; i < to && i < text.Count; i++)
        {
            builder.Append(text.Chars[i].Text);
        }

        return Clean(builder.ToString(), fromEnd);
    }

    /// <summary>
    /// A label: NFC, one line, without leaders, colons or brackets around it, at most 40 characters (the end nearest the
    /// field kept).
    /// </summary>
    private static string Clean(string raw, bool fromEnd)
    {
        var words = TextNormalizer.ToNfc(raw).Replace('\r', ' ').Replace('\n', ' ');
        var builder = new StringBuilder();
        foreach (var c in words)
        {
            builder.Append(IsLeader(c.ToString()) ? ' ' : c);
        }

        var label = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', ':', '(', ')', '-', '–', ',', ';');
        if (label.Length > MaxLabelLength)
        {
            label = fromEnd ? label[^MaxLabelLength..].TrimStart() : label[..MaxLabelLength].TrimEnd();
        }

        return label;
    }

    private static bool Overlaps(PdfRect a, PdfRect b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var height = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller > 0 && width * height > 0.3 * smaller;
    }
}

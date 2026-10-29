using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Security;

/// <summary>What else went with the removed areas.</summary>
/// <param name="Structure">Texts of the tag structure (alternate, actual and expansion texts, titles) removed.</param>
internal sealed record CleanupResult(int Annotations, int Fields, int Bookmarks, IReadOnlyList<string> Metadata, bool Xmp, int Structure);

/// <summary>
/// After areas are removed, what else shows their content goes too (SPEC P8 B4): annotations and links over an area or
/// whose text, address or rich text names what was removed, form fields with a widget over an area or such a value,
/// bookmarks whose title names it (their children move up), document information entries and the XMP metadata stream
/// that contain it, and the texts of the tag structure (what screen readers read and copying may take: /Alt,
/// /ActualText, /E, /T) that name it. Matching is on NFC text, case-insensitive, white space collapsed, and on digits alone for numbers.
/// </summary>
internal static class RedactionCleanup
{
    /// <summary>Removed text shorter than this (letters and digits) is not looked for elsewhere: it would match too much.</summary>
    public const int MinMatchLength = 4;

    private static readonly string[] AnnotationTexts = ["/Contents", "/Subj", "/RC", "/TU", "/T"];
    private static readonly string[] InfoKeys = ["/Title", "/Author", "/Subject", "/Keywords", "/Creator", "/Producer"];
    private static readonly string[] StructureTexts = ["/Alt", "/ActualText", "/E", "/T"];

    /// <summary>Upper bound for the tag structure elements visited (defends against hostile files).</summary>
    private const int MaxStructureElements = 500_000;

    public static CleanupResult Apply(PdfDocument document, IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> areas, IReadOnlyList<string> removedTexts)
    {
        var needles = Needles(removedTexts);
        var fields = RemoveFields(document, areas, needles);
        var annotations = RemoveAnnotations(document, areas, needles);
        var bookmarks = RemoveBookmarks(document, needles);
        var (metadata, xmp) = RemoveMetadata(document, needles);
        var structure = RemoveStructureTexts(document, needles);
        return new CleanupResult(annotations, fields, bookmarks, metadata, xmp, structure);
    }

    /// <summary>The forms of removed text looked for: as written and, for numbers, digits only.</summary>
    internal static IReadOnlyList<string> Needles(IReadOnlyList<string> removedTexts)
    {
        var needles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in removedTexts)
        {
            if (text.Count(char.IsLetterOrDigit) < MinMatchLength)
            {
                continue;
            }

            needles.Add(Fold(text));
            var digits = new string([.. text.Where(char.IsAsciiDigit)]);
            if (digits.Length >= MinMatchLength && digits.Length * 2 >= text.Count(char.IsLetterOrDigit))
            {
                needles.Add(digits);
            }
        }

        return [.. needles];
    }

    /// <summary>Whether <paramref name="value"/> contains any of <paramref name="needles"/>.</summary>
    internal static bool Names(string? value, IReadOnlyList<string> needles)
    {
        if (string.IsNullOrEmpty(value) || needles.Count == 0)
        {
            return false;
        }

        var folded = Fold(value);
        var digits = new string([.. value.Where(char.IsAsciiDigit)]);
        return needles.Any(n => folded.Contains(n, StringComparison.Ordinal) || (n.All(char.IsAsciiDigit) && digits.Contains(n, StringComparison.Ordinal)));
    }

    private static string Fold(string text)
    {
        var builder = new StringBuilder();
        var space = false;
        foreach (var c in TextNormalizer.ToNfc(text).ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool Over(PdfRect rect, IReadOnlyList<PdfRect> areas)
        => areas.Any(a => rect.Left < a.Right && a.Left < rect.Right && rect.Bottom < a.Top && a.Bottom < rect.Top);

    private static int RemoveFields(PdfDocument document, IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> areas, IReadOnlyList<string> needles)
    {
        var names = FormReader.Read(document)
            .Where(f => Names(f.Value, needles) || f.Widgets.Any(w => areas.TryGetValue(w.Page, out var rects) && Over(w.Rect, rects)))
            .Select(f => f.Name)
            .ToList();
        if (names.Count > 0)
        {
            FormWriter.RemoveFields(document, names);
        }

        return names.Count;
    }

    private static int RemoveAnnotations(PdfDocument document, IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> areas, IReadOnlyList<string> needles)
    {
        var removed = 0;
        for (var index = 0; index < document.PageCount; index++)
        {
            var page = document.Pages[index];
            if (Annotations(page) is not { } annotations)
            {
                continue;
            }

            areas.TryGetValue(index, out var rects);
            var gone = new List<PdfDictionary>();
            foreach (var annotation in annotations.Elements.Select(AsDictionary).OfType<PdfDictionary>())
            {
                if (Name(annotation, "/Subtype") is "/Widget" or "/Popup")
                {
                    continue; // fields went with their widgets; pop-ups go with their annotation
                }

                var over = rects is not null && RectOf(annotation) is { } rect && Over(rect, rects);
                if (over || AnnotationTexts.Any(key => Names(annotation.Elements.GetString(key), needles)) || Names(Uri(annotation), needles))
                {
                    gone.Add(annotation);
                }
            }

            if (gone.Count > 0)
            {
                AnnotationThreads.Remove(document, page, gone);
                removed += gone.Count;
            }
        }

        return removed;
    }

    private static PdfRect? RectOf(PdfDictionary annotation)
    {
        if (AsArray(annotation.Elements["/Rect"]) is not { Elements.Count: 4 } array)
        {
            return null;
        }

        var values = array.Elements.Select(e => Resolve(e) switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            _ => double.NaN,
        }).ToArray();
        return values.Any(double.IsNaN)
            ? null
            : new PdfRect(Math.Min(values[0], values[2]), Math.Min(values[1], values[3]), Math.Max(values[0], values[2]), Math.Max(values[1], values[3]));
    }

    private static string? Uri(PdfDictionary annotation)
        => AsDictionary(annotation.Elements["/A"]) is { } action ? action.Elements.GetString("/URI") : null;

    private static int RemoveBookmarks(PdfDocument document, IReadOnlyList<string> needles)
    {
        if (needles.Count == 0 || AsDictionary(document.Internals.Catalog.Elements["/Outlines"]) is not { } outlines)
        {
            return 0;
        }

        var removed = 0;
        var budget = OutlineItems.MaxItems;
        Prune(outlines, depth: 0);
        if (removed > 0)
        {
            OutlineItems.Finish(document, outlines);
        }

        return removed;

        void Prune(PdfDictionary parent, int depth)
        {
            var kept = new List<PdfDictionary>();
            foreach (var child in OutlineItems.Children(parent))
            {
                if (--budget < 0 || depth >= OutlineItems.MaxDepth)
                {
                    kept.Add(child);
                    continue;
                }

                Prune(child, depth + 1);
                if (Names(child.Elements.GetString("/Title"), needles))
                {
                    // The item goes; its children take its place.
                    removed++;
                    kept.AddRange(OutlineItems.Children(child));
                }
                else
                {
                    kept.Add(child);
                }
            }

            OutlineItems.Relink(parent, kept);
        }
    }

    private static (IReadOnlyList<string> Keys, bool Xmp) RemoveMetadata(PdfDocument document, IReadOnlyList<string> needles)
    {
        var keys = new List<string>();
        if (needles.Count == 0)
        {
            return (keys, false);
        }

        var info = document.Info.Elements;
        foreach (var key in InfoKeys.Concat(info.Keys.Where(k => !InfoKeys.Contains(k) && k is not ("/CreationDate" or "/ModDate" or "/Trapped"))).ToList())
        {
            if (info.ContainsKey(key) && Names(info.GetString(key), needles))
            {
                info.Remove(key);
                keys.Add(key.TrimStart('/'));
            }
        }

        var catalog = document.Internals.Catalog;
        var xmp = false;
        if (AsDictionary(catalog.Elements["/Metadata"]) is { Stream: { } stream })
        {
            var bytes = stream.UnfilteredValue;
            var text = Encoding.UTF8.GetString(bytes);
            if (Names(System.Net.WebUtility.HtmlDecode(text), needles))
            {
                catalog.Elements.Remove("/Metadata");
                xmp = true;
            }
        }

        return (keys, xmp);
    }

    /// <summary>
    /// Texts of the tag structure naming what was removed go; the elements stay (their content is what the page shows).
    /// Only /K is followed, so marked-content and object references (to pages, annotations) are not entered.
    /// </summary>
    private static int RemoveStructureTexts(PdfDocument document, IReadOnlyList<string> needles)
    {
        if (needles.Count == 0 || AsDictionary(document.Internals.Catalog.Elements["/StructTreeRoot"]) is not { } root)
        {
            return 0;
        }

        var removed = 0;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<PdfItem?>();
        pending.Push(root.Elements["/K"]);
        while (pending.Count > 0 && visited.Count < MaxStructureElements)
        {
            switch (Resolve(pending.Pop()))
            {
                case PdfArray array:
                    foreach (var item in array.Elements)
                    {
                        pending.Push(item);
                    }

                    break;
                case PdfDictionary element when visited.Add(element):
                    foreach (var key in StructureTexts)
                    {
                        if (element.Elements.ContainsKey(key) && Names(element.Elements.GetString(key), needles))
                        {
                            element.Elements.Remove(key);
                            removed++;
                        }
                    }

                    pending.Push(element.Elements["/K"]);
                    break;
            }
        }

        return removed;
    }
}

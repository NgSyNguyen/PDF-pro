using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace PdfPro.Core.Splitting;

/// <summary>What a split file's name can be made of.</summary>
/// <param name="Name">The document's file name without extension.</param>
/// <param name="Index">1-based number of the file in the split.</param>
/// <param name="Title">Bookmark title or matched text, or null.</param>
/// <param name="FirstPage">1-based number of its first page.</param>
/// <param name="LastPage">1-based number of its last page.</param>
public sealed record FileNameValues(string Name, int Index, string? Title, int FirstPage, int LastPage);

public enum FileNameTemplateError
{
    /// <summary>The pattern is empty.</summary>
    Empty,

    /// <summary>A "{" has no matching "}" (or the reverse).</summary>
    UnbalancedBrace,

    /// <summary>A {…} part is not one of the known fields.</summary>
    UnknownField,
}

/// <summary>
/// A naming pattern for split files: {name} (document name), {index} or {index:000} (file number, zero-padded to the
/// number of zeros), {bookmark} and {match} (the file's bookmark title or matched text), {first} and {last} (page
/// numbers). Field names ignore case. The result is made safe with <see cref="FileNames.Sanitize"/>; when nothing is
/// left (no title for this file) the <see cref="Default"/> pattern is used.
/// </summary>
public sealed class FileNameTemplate
{
    public const string DefaultPattern = "{name}_{index:000}";

    public const string BookmarkPattern = "{bookmark}";

    public const string MatchPattern = "{name}_{match}";

    private readonly Part[] _parts;

    private FileNameTemplate(string pattern, Part[] parts)
    {
        Pattern = pattern;
        _parts = parts;
    }

    public static FileNameTemplate Default { get; } = Parse(DefaultPattern);

    public string Pattern { get; }

    /// <summary>True when the pattern uses the file's title ({bookmark} or {match}).</summary>
    public bool UsesTitle => _parts.Any(p => p.Field is Field.Title);

    public static bool TryParse(
        string? pattern,
        [NotNullWhen(true)] out FileNameTemplate? template,
        [NotNullWhen(false)] out FileNameTemplateError? error,
        out string? token)
    {
        template = null;
        token = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = FileNameTemplateError.Empty;
            return false;
        }

        var parts = new List<Part>();
        var literal = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '}')
            {
                error = FileNameTemplateError.UnbalancedBrace;
                token = "}";
                return false;
            }

            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var end = pattern.IndexOf('}', i + 1);
            var open = pattern.IndexOf('{', i + 1);
            if (end < 0 || (open >= 0 && open < end))
            {
                error = FileNameTemplateError.UnbalancedBrace;
                token = pattern[i..(end < 0 ? pattern.Length : end + 1)];
                return false;
            }

            var field = pattern[(i + 1)..end];
            if (!TryReadField(field, out var part))
            {
                error = FileNameTemplateError.UnknownField;
                token = pattern[i..(end + 1)];
                return false;
            }

            if (literal.Length > 0)
            {
                parts.Add(new Part(Field.Literal, literal.ToString(), 0));
                literal.Clear();
            }

            parts.Add(part);
            i = end;
        }

        if (literal.Length > 0)
        {
            parts.Add(new Part(Field.Literal, literal.ToString(), 0));
        }

        template = new FileNameTemplate(pattern, [.. parts]);
        error = null;
        return true;
    }

    /// <exception cref="FormatException">The pattern is not valid.</exception>
    public static FileNameTemplate Parse(string pattern)
        => TryParse(pattern, out var template, out var error, out var token)
            ? template
            : throw new FormatException($"Invalid file name pattern ({error}): '{token}'.");

    /// <summary>The file name (without extension) for one file: safe for Windows and never empty.</summary>
    public string Render(FileNameValues values, int maxLength = FileNames.DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(values);
        var name = FileNames.Sanitize(RenderRaw(values), maxLength);
        if (name.Length > 0)
        {
            return name;
        }

        name = ReferenceEquals(this, Default) ? string.Empty : Default.Render(values, maxLength);
        return name.Length > 0 ? name : values.Index.ToString(CultureInfo.InvariantCulture);
    }

    private string RenderRaw(FileNameValues values)
    {
        var text = new StringBuilder();
        foreach (var part in _parts)
        {
            text.Append(part.Field switch
            {
                Field.Literal => part.Text,
                Field.Name => values.Name,
                Field.Index => values.Index.ToString(new string('0', Math.Max(part.Digits, 1)), CultureInfo.InvariantCulture),
                Field.Title => values.Title ?? string.Empty,
                Field.First => values.FirstPage.ToString(CultureInfo.InvariantCulture),
                Field.Last => values.LastPage.ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"Unexpected field {part.Field}."),
            });
        }

        // A pattern made only of the title (e.g. "{bookmark}") gives nothing for a file without title: fall back.
        return _parts.All(p => p.Field is Field.Title or Field.Literal) && string.IsNullOrWhiteSpace(values.Title)
            ? string.Empty
            : text.ToString();
    }

    private static bool TryReadField(string field, out Part part)
    {
        var colon = field.IndexOf(':', StringComparison.Ordinal);
        var name = (colon < 0 ? field : field[..colon]).Trim().ToLowerInvariant();
        var format = colon < 0 ? null : field[(colon + 1)..].Trim();
        part = default;
        if (format is not null && (name != "index" || format.Length == 0 || format.Length > 9 || format.Any(c => c != '0')))
        {
            return false;
        }

        var kind = name switch
        {
            "name" => Field.Name,
            "index" => Field.Index,
            "bookmark" or "match" => Field.Title,
            "first" => Field.First,
            "last" => Field.Last,
            _ => (Field?)null,
        };
        if (kind is null)
        {
            return false;
        }

        part = new Part(kind.Value, string.Empty, format?.Length ?? 1);
        return true;
    }

    private enum Field
    {
        Literal,
        Name,
        Index,
        Title,
        First,
        Last,
    }

    private readonly record struct Part(Field Field, string Text, int Digits);
}

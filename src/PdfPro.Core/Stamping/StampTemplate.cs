using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using PdfPro.Core.Text;

namespace PdfPro.Core.Stamping;

public enum StampTemplateError
{
    /// <summary>A "{" has no matching "}" (or the reverse).</summary>
    UnbalancedBrace,

    /// <summary>A {…} part is not one of the known fields.</summary>
    UnknownField,

    /// <summary>A date format is empty or longer than 40 characters.</summary>
    InvalidDateFormat,
}

/// <summary>What the fields of a header, footer or page number stand for on one page.</summary>
/// <param name="Page">The page's number as printed (already in its style: "3", "iii").</param>
/// <param name="Pages">The last printed number.</param>
/// <param name="Date">Today (or a fixed date).</param>
/// <param name="FileName">The document's file name without extension.</param>
/// <param name="Title">The document title, or null.</param>
public sealed record StampValues(string Page, string Pages, DateTime Date, string FileName, string? Title);

/// <summary>
/// Text with fields for headers, footers and page numbers: {page}, {pages}, {date} or {date:dd/MM/yyyy} (any .NET date
/// format, default dd/MM/yyyy), {filename}, {title}. Field names ignore case; "{{" and "}}" write braces. The result
/// is NFC.
/// </summary>
public sealed class StampTemplate
{
    public const string DefaultDateFormat = "dd/MM/yyyy";

    private const int MaxDateFormatLength = 40;

    private readonly Part[] _parts;

    private StampTemplate(string pattern, Part[] parts)
    {
        Pattern = pattern;
        _parts = parts;
    }

    private enum Field
    {
        Literal,
        Page,
        Pages,
        Date,
        FileName,
        Title,
    }

    public string Pattern { get; }

    /// <summary>True when the text changes from page to page ({page}).</summary>
    public bool DependsOnPage => _parts.Any(p => p.Field == Field.Page);

    public static bool TryParse(
        string? pattern,
        [NotNullWhen(true)] out StampTemplate? template,
        [NotNullWhen(false)] out StampTemplateError? error,
        out string? token)
    {
        template = null;
        token = null;
        var text = pattern ?? string.Empty;
        var parts = new List<Part>();
        var literal = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c)
            {
                literal.Append(c);
                i++;
                continue;
            }

            if (c == '}')
            {
                error = StampTemplateError.UnbalancedBrace;
                token = "}";
                return false;
            }

            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var end = text.IndexOf('}', i + 1);
            var open = text.IndexOf('{', i + 1);
            if (end < 0 || (open >= 0 && open < end))
            {
                error = StampTemplateError.UnbalancedBrace;
                token = text[i..(end < 0 ? text.Length : end + 1)];
                return false;
            }

            token = text[i..(end + 1)];
            if (!TryReadField(text[(i + 1)..end], out var part, out error))
            {
                return false;
            }

            if (literal.Length > 0)
            {
                parts.Add(new Part(Field.Literal, literal.ToString()));
                literal.Clear();
            }

            parts.Add(part);
            i = end;
        }

        if (literal.Length > 0)
        {
            parts.Add(new Part(Field.Literal, literal.ToString()));
        }

        token = null;
        error = null;
        template = new StampTemplate(text, [.. parts]);
        return true;
    }

    /// <summary>A pattern that writes <paramref name="literal"/> as it is (braces doubled).</summary>
    public static string Escape(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return literal.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
    }

    /// <exception cref="FormatException">The pattern is not valid.</exception>
    public static StampTemplate Parse(string pattern)
        => TryParse(pattern, out var template, out var error, out var token)
            ? template
            : throw new FormatException($"Invalid stamp text ({error}): '{token}'.");

    public string Render(StampValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var text = new StringBuilder();
        foreach (var part in _parts)
        {
            text.Append(part.Field switch
            {
                Field.Literal => part.Value,
                Field.Page => values.Page,
                Field.Pages => values.Pages,
                Field.Date => values.Date.ToString(part.Value, CultureInfo.InvariantCulture),
                Field.FileName => values.FileName,
                _ => values.Title ?? string.Empty,
            });
        }

        return TextNormalizer.ToNfc(text.ToString());
    }

    private static bool TryReadField(string field, [NotNullWhen(true)] out Part? part, [NotNullWhen(false)] out StampTemplateError? error)
    {
        part = null;
        error = null;
        var colon = field.IndexOf(':', StringComparison.Ordinal);
        var name = (colon < 0 ? field : field[..colon]).Trim().ToLowerInvariant();
        var argument = colon < 0 ? null : field[(colon + 1)..];
        if (name == "date")
        {
            var format = argument ?? DefaultDateFormat;
            if (format.Length is 0 or > MaxDateFormatLength)
            {
                error = StampTemplateError.InvalidDateFormat;
                return false;
            }

            part = new Part(Field.Date, format);
            return true;
        }

        Field? kind = argument is not null ? null : name switch
        {
            "page" => Field.Page,
            "pages" => Field.Pages,
            "filename" => Field.FileName,
            "title" => Field.Title,
            _ => null,
        };
        if (kind is null)
        {
            error = StampTemplateError.UnknownField;
            return false;
        }

        part = new Part(kind.Value, string.Empty);
        return true;
    }

    private sealed record Part(Field Field, string Value);
}

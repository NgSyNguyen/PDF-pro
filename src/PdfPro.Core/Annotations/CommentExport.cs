using System.Globalization;
using System.Text;
using PdfPro.Core.Text;

namespace PdfPro.Core.Annotations;

/// <summary>An answer in a comment's conversation, for the comments list and its export.</summary>
public sealed record CommentReplyRecord(string Author, DateTimeOffset? Modified, string Contents);

/// <summary>A comment of the document as listed and exported: where, what, who, when, what it says and the answers.</summary>
/// <param name="PageNumber">1-based page.</param>
/// <param name="Kind">What it is, in the reader's language ("Tô sáng").</param>
/// <param name="MarkedText">The text it marks, or null.</param>
/// <param name="State">Its review state in the reader's language, or null for none.</param>
public sealed record CommentRecord(
    int PageNumber,
    string Kind,
    string Author,
    DateTimeOffset? Modified,
    string Contents,
    string? MarkedText,
    string? State,
    IReadOnlyList<CommentReplyRecord> Replies);

/// <summary>The words of the export in the reader's language.</summary>
public sealed record CommentExportLabels(
    string Title,
    string Page,
    string Kind,
    string Author,
    string Date,
    string Contents,
    string MarkedText,
    string State,
    string Reply,
    string NoComments);

/// <summary>
/// Writes the comments of a document as Markdown (to read or paste into a report) or CSV (for a spreadsheet). Text is
/// composed (NFC); dates use the reader's culture in Markdown and ISO 8601 in CSV. CSV cells that a spreadsheet would
/// run as a formula are quoted with a leading apostrophe.
/// </summary>
public static class CommentExport
{
    public static string ToMarkdown(string documentName, IReadOnlyList<CommentRecord> comments, CommentExportLabels labels, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(culture);
        var builder = new StringBuilder();
        builder.Append("# ").Append(Inline(labels.Title)).Append(" — ").Append(Inline(documentName)).Append('\n');
        if (comments.Count == 0)
        {
            builder.Append('\n').Append(Inline(labels.NoComments)).Append('\n');
            return builder.ToString();
        }

        foreach (var page in comments.GroupBy(c => c.PageNumber).OrderBy(g => g.Key))
        {
            builder.Append('\n').Append("## ").Append(Inline(labels.Page)).Append(' ').Append(page.Key.ToString(culture)).Append("\n\n");
            foreach (var comment in page)
            {
                builder.Append("- **").Append(Inline(comment.Kind)).Append("** · ").Append(Inline(comment.Author));
                if (comment.Modified is { } date)
                {
                    builder.Append(" · ").Append(date.ToString("g", culture));
                }

                if (comment.State is { Length: > 0 } state)
                {
                    builder.Append(" · *").Append(Inline(state)).Append('*');
                }

                builder.Append('\n');
                if (comment.MarkedText is { Length: > 0 } marked)
                {
                    builder.Append("  > ").Append(Inline(marked)).Append('\n');
                }

                if (comment.Contents.Length > 0)
                {
                    foreach (var line in Lines(comment.Contents))
                    {
                        builder.Append("  ").Append(line).Append('\n');
                    }
                }

                foreach (var reply in comment.Replies)
                {
                    builder.Append("  - ").Append(Inline(reply.Author));
                    if (reply.Modified is { } replied)
                    {
                        builder.Append(" · ").Append(replied.ToString("g", culture));
                    }

                    builder.Append(": ").Append(Inline(reply.Contents)).Append('\n');
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>One row per comment and one per answer (its kind is <see cref="CommentExportLabels.Reply"/>).</summary>
    public static string ToCsv(IReadOnlyList<CommentRecord> comments, CommentExportLabels labels)
    {
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(labels);
        var builder = new StringBuilder();
        Row(builder, labels.Page, labels.Kind, labels.Author, labels.Date, labels.Contents, labels.MarkedText, labels.State);
        foreach (var comment in comments)
        {
            var page = comment.PageNumber.ToString(CultureInfo.InvariantCulture);
            Row(builder, page, comment.Kind, comment.Author, Iso(comment.Modified), comment.Contents, comment.MarkedText ?? string.Empty, comment.State ?? string.Empty);
            foreach (var reply in comment.Replies)
            {
                Row(builder, page, labels.Reply, reply.Author, Iso(reply.Modified), reply.Contents, string.Empty, string.Empty);
            }
        }

        return builder.ToString();
    }

    private static string Iso(DateTimeOffset? date) => date?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) ?? string.Empty;

    private static void Row(StringBuilder builder, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Cell(cells[i]));
        }

        builder.Append("\r\n");
    }

    private static string Cell(string value)
    {
        var text = TextNormalizer.ToNfc(value);
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text; // a spreadsheet would run it as a formula
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 || text != text.Trim()
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    /// <summary>One line of Markdown: composed, line breaks as spaces, markup characters escaped.</summary>
    private static string Inline(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in TextNormalizer.ToNfc(text).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' '))
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '#' or '|')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>A comment's lines, each escaped (kept as separate lines of the list item).</summary>
    private static IEnumerable<string> Lines(string text)
        => TextNormalizer.ToNfc(text).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Inline);
}

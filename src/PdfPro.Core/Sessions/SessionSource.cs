using PdfPro.Core.Organizing;

namespace PdfPro.Core.Sessions;

public enum SessionSourceKind
{
    /// <summary>The copy of the opened file.</summary>
    Original,

    /// <summary>A file whose pages were inserted.</summary>
    Inserted,

    /// <summary>The output of a content operation (watermark, page size…).</summary>
    Revision,
}

/// <summary>A PDF file inside the session folder that the page plan draws pages from.</summary>
public sealed record SessionSource
{
    /// <param name="id">Source id, see <see cref="SourceIds"/>.</param>
    /// <param name="fileName">Path relative to the session folder, e.g. "sources/s0.pdf".</param>
    /// <param name="kind">Why the file is in the session.</param>
    /// <param name="pageCount">Number of pages of the file (at least one).</param>
    /// <param name="displayName">Name shown to the user (the inserted file's name); personal data, never logged.</param>
    public SessionSource(string id, string fileName, SessionSourceKind kind, int pageCount, string? displayName = null)
    {
        if (!SourceIds.IsValid(id) || id == SourceIds.Blank)
        {
            throw new ArgumentException("A source id is short lowercase ASCII, e.g. \"s1\", and not \"blank\".", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName) || fileName.Split('/', '\\').Any(part => part is ".." or "." or ""))
        {
            throw new ArgumentException("The file name must be a relative path inside the session folder.", nameof(fileName));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown source kind.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        Id = id;
        FileName = fileName;
        Kind = kind;
        PageCount = pageCount;
        DisplayName = displayName;
    }

    public string Id { get; }

    public string FileName { get; }

    public SessionSourceKind Kind { get; }

    public int PageCount { get; }

    public string? DisplayName { get; }
}

using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Merging;

/// <summary>Why a file cannot be merged.</summary>
public enum MergeSourceFailure
{
    /// <summary>The file is password protected and its author does not allow taking its pages (copy + assemble).</summary>
    NotPermitted,
}

public sealed class MergeSourceException(MergeSourceFailure failure)
    : Exception($"The file cannot be merged: {failure}.")
{
    public MergeSourceFailure Failure { get; } = failure;
}

/// <summary>
/// A file to merge, read once when it is added to the list: page count and sizes, bookmarks, whether it was protected.
/// Its password (if any) stays in memory for writing and never appears in <see cref="ToString"/>.
/// </summary>
public sealed class MergeSource
{
    internal MergeSource(string path, string? password, IReadOnlyList<PdfSize> pageSizes, IReadOnlyList<PdfBookmark> bookmarks, bool isEncrypted)
    {
        Path = path;
        Password = password;
        PageSizes = pageSizes;
        Bookmarks = bookmarks;
        IsEncrypted = isEncrypted;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Full path of the file.</summary>
    public string Path { get; }

    /// <summary>The file name without extension (NFC): the default title of the file in bookmarks.</summary>
    public string Name { get; }

    public int PageCount => PageSizes.Count;

    /// <summary>Displayed size of every page.</summary>
    public IReadOnlyList<PdfSize> PageSizes { get; }

    public IReadOnlyList<PdfBookmark> Bookmarks { get; }

    public bool IsEncrypted { get; }

    internal string? Password { get; }

    /// <summary>
    /// Reads a file to merge. A protected file needs its password, and its author must allow copying and assembling
    /// pages (or the owner password must be given).
    /// </summary>
    /// <exception cref="PdfDocumentException">The file cannot be opened, or the password is missing or wrong.</exception>
    /// <exception cref="MergeSourceException">The author does not allow taking pages out of the file.</exception>
    public static async Task<MergeSource> OpenAsync(IPdfDocumentLoader loader, string path, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var document = await loader.LoadAsync(fullPath, password, cancellationToken).ConfigureAwait(false);
        await using (document.ConfigureAwait(false))
        {
            var features = document.Features;
            if (features.IsEncrypted && !(features.CanCopyContent && features.CanAssemble))
            {
                throw new MergeSourceException(MergeSourceFailure.NotPermitted);
            }

            if (document.PageCount == 0)
            {
                throw new PdfDocumentException(PdfOpenFailure.Corrupted, "The PDF document has no pages.");
            }

            var bookmarks = await document.GetBookmarksAsync(cancellationToken).ConfigureAwait(false);
            return new MergeSource(fullPath, features.IsEncrypted ? password : null, document.PageSizes, bookmarks, features.IsEncrypted);
        }
    }

    /// <summary>A display title (NFC, trimmed) for this file.</summary>
    internal string TitleOr(string? title) => TextNormalizer.ToNfc(string.IsNullOrWhiteSpace(title) ? Name : title.Trim());

    public override string ToString() => $"MergeSource ({PageCount} pages)";
}

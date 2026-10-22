namespace PdfPro.Core.Organizing;

/// <summary>
/// Marks a page of the plan as the first page of a new file when the document is split by marks ("Trang đầu tệp"):
/// the marked page and the pages after it, up to the next mark, become one file. The first page of the document always
/// starts the first file; marking it only gives that file a name. Marks are not written into the PDF.
/// </summary>
public sealed record SplitMark
{
    /// <summary>Longest file name the user may give, in characters.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>A mark without a chosen file name (the split dialog's naming pattern is used).</summary>
    public static readonly SplitMark Unnamed = new();

    /// <param name="fileName">The output file name the user typed (without folder or extension), or null.</param>
    public SplitMark(string? fileName = null)
    {
        if (fileName is not null)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("A file name must not be blank; use null for no name.", nameof(fileName));
            }

            ArgumentOutOfRangeException.ThrowIfGreaterThan(fileName.Length, MaxFileNameLength, nameof(fileName));
        }

        FileName = fileName;
    }

    public string? FileName { get; }
}

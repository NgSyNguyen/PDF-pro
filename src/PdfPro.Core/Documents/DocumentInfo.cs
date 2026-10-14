namespace PdfPro.Core.Documents;

/// <summary>
/// Facts about a document that operations need for validation. Extended as later phases need more.
/// </summary>
public sealed record DocumentInfo
{
    public DocumentInfo(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        PageCount = pageCount;
    }

    public int PageCount { get; }

    public string? FilePath { get; init; }

    public bool IsEncrypted { get; init; }

    public bool HasDigitalSignatures { get; init; }
}

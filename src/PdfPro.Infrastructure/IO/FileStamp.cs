namespace PdfPro.Infrastructure.IO;

/// <summary>
/// Size and last write time of a file, remembered when a document is opened or saved, to notice that another program
/// changed the file meanwhile.
/// </summary>
public readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc)
{
    /// <summary>The file's current stamp, or null when it does not exist (or cannot be read).</summary>
    public static FileStamp? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

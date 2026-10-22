namespace PdfPro.Core.Splitting;

/// <summary>Names the files of a split: the user's own name for a file, otherwise the pattern; never two alike.</summary>
public sealed class SplitNaming
{
    /// <param name="template">The naming pattern.</param>
    /// <param name="documentName">The split document's file name without extension ({name}).</param>
    public SplitNaming(FileNameTemplate template, string documentName)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(documentName);
        Template = template;
        DocumentName = documentName;
    }

    public FileNameTemplate Template { get; }

    public string DocumentName { get; }

    /// <summary>The file name (without extension) of each file, in order, made unique.</summary>
    /// <param name="isTaken">True for a name already used outside the split (an existing file); null for none.</param>
    public IReadOnlyList<string> Names(IReadOnlyList<SplitSegment> segments, Func<string, bool>? isTaken = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var names = new string[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            names[i] = NameOf(segments[i], i + 1);
        }

        return FileNames.MakeUnique(names, isTaken);
    }

    /// <summary>The name of one file before names are made unique.</summary>
    /// <param name="index">1-based file number.</param>
    public string NameOf(SplitSegment segment, int index)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var values = new FileNameValues(DocumentName, index, segment.Title, segment.First + 1, segment.Last + 1);
        var chosen = FileNames.Sanitize(segment.FileName);
        return chosen.Length > 0 ? chosen : Template.Render(values);
    }
}

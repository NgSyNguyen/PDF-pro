using PdfPro.Core.Operations;

namespace PdfPro.Core.Organizing;

/// <summary>What to write: a plan, the files it draws pages from and the source whose structure the output keeps.</summary>
public sealed record MaterializeRequest
{
    public required PagePlan Plan { get; init; }

    /// <summary>
    /// The source the output is built on (the opened file or the latest revision): its bookmarks, forms, metadata and
    /// named destinations are kept; pages of other sources are imported into it.
    /// </summary>
    public required string BaseSourceId { get; init; }

    /// <summary>Full path of every source file, by source id; must cover the base source and every page of the plan.</summary>
    public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }

    /// <summary>Folder for intermediate files, e.g. repaired copies of sources that cannot be read as they are.</summary>
    public required string WorkingDirectory { get; init; }
}

/// <summary>Turns a page plan into a real PDF file (SPEC "PagePlan — tài liệu ảo": done on save and before content operations).</summary>
public interface IPlanMaterializer
{
    /// <summary>Writes the plan as a complete PDF to <paramref name="output"/>.</summary>
    /// <exception cref="MaterializationException">A source cannot be read or the output cannot be built.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the output was written.</exception>
    Task MaterializeAsync(MaterializeRequest request, Stream output, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

public enum MaterializationFailure
{
    /// <summary>A source file cannot be parsed, even after an attempt to repair it.</summary>
    SourceUnreadable,

    /// <summary>A source file is password protected.</summary>
    SourceEncrypted,

    /// <summary>A source file is missing or cannot be opened.</summary>
    SourceMissing,
}

/// <summary>Writing a plan failed; <see cref="SourceId"/> names the source at fault (never a path: logs stay private).</summary>
public sealed class MaterializationException : Exception
{
    public MaterializationException(MaterializationFailure failure, string sourceId, Exception? innerException = null)
        : base($"Materializing the page plan failed: {failure} (source {sourceId}).", innerException)
    {
        Failure = failure;
        SourceId = sourceId;
    }

    public MaterializationFailure Failure { get; }

    public string SourceId { get; }
}

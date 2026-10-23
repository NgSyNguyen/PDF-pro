using System.Text.Json;
using PdfPro.Core.Sessions;

namespace PdfPro.Infrastructure.Sessions;

/// <summary>
/// manifest.json of a session folder (SPEC "DocumentSession và Revision"): sources, current state and the replayable
/// undo/redo history, written after every change so a crashed session can be restored exactly. Contains the
/// original file path (personal data, like the recent files list): it stays in the user profile and is never logged.
/// </summary>
public sealed record SessionManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid SessionId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }

    public string? OriginalPath { get; init; }

    /// <summary>The original file as last opened or saved, to notice changes made by other programs.</summary>
    public FileStampDto? OriginalStamp { get; init; }

    public required IReadOnlyList<SourceDto> Sources { get; init; }

    public required StateDto State { get; init; }

    /// <summary>The state before the oldest undoable step.</summary>
    public required StateDto HistoryBase { get; init; }

    /// <summary>Undoable steps, oldest first.</summary>
    public required IReadOnlyList<RecordDto> Undo { get; init; }

    /// <summary>Redoable steps, next redo first.</summary>
    public required IReadOnlyList<RecordDto> Redo { get; init; }

    public int? SavePoint { get; init; }

    public bool ForcedDirty { get; init; }

    /// <summary>A save that was swapping files when the manifest was written; repaired at the next start if still there.</summary>
    public PendingSaveDto? PendingSave { get; init; }

    /// <summary>True when the state differs from the saved file.</summary>
    public bool IsDirty => ForcedDirty || SavePoint != Undo.Count;
}

public sealed record FileStampDto(long Length, DateTime LastWriteTimeUtc);

public sealed record SourceDto(string Id, string File, SessionSourceKind Kind, int PageCount, string? DisplayName = null);

public sealed record StateDto(string BaseSourceId, int Revision, IReadOnlyList<PageDto> Plan);

/// <param name="Source">Source id ("blank" for blank pages).</param>
/// <param name="Page">0-based page index in the source.</param>
/// <param name="Rotation">Extra clockwise rotation in degrees.</param>
/// <param name="Crop">Kept area (left, top, width, height) in points on the displayed page, or null.</param>
/// <param name="Key">Page key.</param>
/// <param name="BlankSize">Width and height of a blank page, or null.</param>
/// <param name="Split">Set when the page starts a file in a split by marks.</param>
public sealed record PageDto(
    string Source,
    int Page,
    int Rotation,
    Guid Key,
    IReadOnlyList<double>? Crop = null,
    IReadOnlyList<double>? BlankSize = null,
    SplitMarkDto? Split = null);

/// <param name="FileName">The file name the user chose for the file the page starts, or null.</param>
public sealed record SplitMarkDto(string? FileName = null);

/// <param name="Operation">Operation id, e.g. "organize.rotate".</param>
/// <param name="Parameters">The operation's parameter record as JSON.</param>
/// <param name="NewPageKeys">Keys of the pages the step created.</param>
/// <param name="NewSourceId">Revision source a content operation produced, or null.</param>
public sealed record RecordDto(string Operation, JsonElement Parameters, IReadOnlyList<Guid> NewPageKeys, string? NewSourceId = null);

/// <param name="TempPath">The finished new file waiting to replace the original.</param>
/// <param name="BackupPath">Where the original goes during the swap.</param>
public sealed record PendingSaveDto(string TargetPath, string TempPath, string? BackupPath = null);

/// <summary>A manifest that cannot be read or does not describe a valid session.</summary>
public sealed class SessionManifestException(string message, Exception? innerException = null) : Exception(message, innerException);

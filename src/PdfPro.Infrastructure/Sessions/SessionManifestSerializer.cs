using System.Text.Json;
using System.Text.Json.Serialization;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.IO;

namespace PdfPro.Infrastructure.Sessions;

/// <summary>Converts session snapshots to manifests and JSON, and back.</summary>
public static class SessionManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <param name="snapshot">The session.</param>
    /// <param name="createdUtc">When the session started.</param>
    /// <param name="originalStamp">The original file as last opened or saved.</param>
    /// <param name="pendingSave">A save being swapped in right now, if any.</param>
    public static SessionManifest ToManifest(SessionSnapshot snapshot, DateTimeOffset createdUtc, FileStamp? originalStamp = null, PendingSaveDto? pendingSave = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SessionManifest
        {
            SessionId = snapshot.SessionId,
            CreatedUtc = createdUtc,
            UpdatedUtc = DateTimeOffset.UtcNow,
            OriginalPath = snapshot.OriginalPath,
            OriginalStamp = originalStamp is { } stamp ? new FileStampDto(stamp.Length, stamp.LastWriteTimeUtc) : null,
            Sources = [.. snapshot.Sources.Select(s => new SourceDto(s.Id, s.FileName, s.Kind, s.PageCount, s.DisplayName))],
            State = ToDto(snapshot.State),
            HistoryBase = ToDto(snapshot.HistoryBase),
            Undo = [.. snapshot.Undo.Select(ToDto)],
            Redo = [.. snapshot.Redo.Select(ToDto)],
            SavePoint = snapshot.SavePoint,
            ForcedDirty = snapshot.ForcedDirty,
            PendingSave = pendingSave,
        };
    }

    public static string Serialize(SessionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    /// <exception cref="SessionManifestException">Not valid JSON, or written by a newer version of the app.</exception>
    public static SessionManifest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        SessionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SessionManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new SessionManifestException("The session manifest is not valid JSON.", ex);
        }

        if (manifest is null)
        {
            throw new SessionManifestException("The session manifest is empty.");
        }

        if (manifest.SchemaVersion > SessionManifest.CurrentSchemaVersion)
        {
            throw new SessionManifestException($"The session manifest uses schema {manifest.SchemaVersion}, newer than this version of the app.");
        }

        return manifest;
    }

    /// <summary>Rebuilds the snapshot; parameters are read as the type each operation declares.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="parameterTypes">Parameter record type of an operation id, or null when the operation is unknown.</param>
    /// <exception cref="SessionManifestException">The manifest does not describe a valid session.</exception>
    public static SessionSnapshot ToSnapshot(SessionManifest manifest, Func<string, Type?> parameterTypes)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        try
        {
            return new SessionSnapshot
            {
                SessionId = manifest.SessionId,
                OriginalPath = manifest.OriginalPath,
                Sources = [.. manifest.Sources.Select(s => new SessionSource(s.Id, s.File, s.Kind, s.PageCount, s.DisplayName))],
                State = FromDto(manifest.State),
                HistoryBase = FromDto(manifest.HistoryBase),
                Undo = [.. manifest.Undo.Select(r => FromDto(r, parameterTypes))],
                Redo = [.. manifest.Redo.Select(r => FromDto(r, parameterTypes))],
                SavePoint = manifest.SavePoint,
                ForcedDirty = manifest.ForcedDirty,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new SessionManifestException("The session manifest does not describe a valid session.", ex);
        }
    }

    private static StateDto ToDto(SessionState state)
        => new(state.BaseSourceId, state.Revision, [.. state.Plan.Select(ToDto)]);

    private static PageDto ToDto(PageRef page) => new(
        page.SourceId,
        page.SourcePageIndex,
        page.RotationDelta,
        page.PageKey,
        page.CropBoxOverride is { } crop ? [crop.Left, crop.Top, crop.Width, crop.Height] : null,
        page.BlankSize is { } size ? [size.Width, size.Height] : null,
        page.SplitMark is { } mark ? new SplitMarkDto(mark.FileName) : null);

    private static RecordDto ToDto(HistoryRecord record) => new(
        record.OperationId,
        JsonSerializer.SerializeToElement(record.Parameters, record.Parameters.GetType(), Options),
        record.NewPageKeys,
        record.NewSourceId);

    private static SessionState FromDto(StateDto state)
        => new(PagePlan.Create(state.Plan.Select(FromDto)), state.BaseSourceId, state.Revision);

    private static PageRef FromDto(PageDto page)
    {
        var crop = page.Crop is { Count: 4 } c ? new PageCrop(c[0], c[1], c[2], c[3]) : (PageCrop?)null;
        var mark = page.Split is { } split ? new SplitMark(split.FileName) : null;
        if (page.BlankSize is { Count: 2 } size)
        {
            return PageRef.Blank(new PdfSize(size[0], size[1]), page.Key).WithRotation(page.Rotation).WithCrop(crop).WithSplitMark(mark);
        }

        return new PageRef(page.Source, page.Page, page.Rotation, crop, page.Key).WithSplitMark(mark);
    }

    private static HistoryRecord FromDto(RecordDto record, Func<string, Type?> parameterTypes)
    {
        var type = parameterTypes(record.Operation)
            ?? throw new InvalidOperationException($"Unknown operation '{record.Operation}'.");
        var parameters = record.Parameters.Deserialize(type, Options)
            ?? throw new InvalidOperationException($"Missing parameters for '{record.Operation}'.");
        return new HistoryRecord(record.Operation, parameters, record.NewPageKeys, record.NewSourceId);
    }
}

using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Materializing;

/// <summary>Resource keys of the materializer's progress stages.</summary>
public static class MaterializeStages
{
    public const string Open = "Stage_MaterializeOpen";
    public const string Pages = "Stage_MaterializePages";
    public const string Write = "Stage_MaterializeWrite";
}

/// <summary>
/// Writes a page plan with PDFsharp (SPEC "PagePlan — tài liệu ảo"). The base source is opened for modification
/// and its page tree rearranged, so its document structure (metadata, bookmarks, named destinations, forms, links)
/// is kept; pages of other sources are imported and their links reconnected; references to deleted pages are cut so
/// those pages really leave the file. Sources PDFsharp cannot parse are first repaired by PDFium.
/// </summary>
public sealed class PdfSharpPlanMaterializer(ISourceRepairer repairer) : IPlanMaterializer
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    public Task MaterializeAsync(MaterializeRequest request, Stream output, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => MaterializeAsync(request, output, progress, finish: null, cancellationToken);

    /// <summary>
    /// Writes the plan, letting <paramref name="finish"/> complete the document (outline, table of contents,
    /// metadata) just before it is saved.
    /// </summary>
    internal async Task MaterializeAsync(
        MaterializeRequest request,
        Stream output,
        IProgress<OperationProgress> progress,
        Action<PdfDocument, IReadOnlyList<PlacedPage>>? finish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        Validate(request);
        progress.Report(new OperationProgress(0, 0, MaterializeStages.Open));

        var documents = new Dictionary<string, PdfDocument>(StringComparer.Ordinal);
        try
        {
            foreach (var sourceId in request.Plan.GetSourceIds().Append(request.BaseSourceId).Distinct().OrderBy(id => id != request.BaseSourceId))
            {
                var mode = sourceId == request.BaseSourceId ? PdfDocumentOpenMode.Modify : PdfDocumentOpenMode.Import;
                var password = request.SourcePasswords?.GetValueOrDefault(sourceId);
                documents[sourceId] = await OpenAsync(sourceId, request.SourcePaths[sourceId], mode, password, request.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            }

            await Task.Run(() => Write(request, documents, output, progress, finish, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var document in documents.Values)
            {
                document.Dispose();
            }
        }
    }

    private static void Validate(MaterializeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Plan, nameof(request));
        if (request.Plan.Count == 0)
        {
            throw new ArgumentException("The plan has no pages.", nameof(request));
        }

        var missing = request.Plan.GetSourceIds().Append(request.BaseSourceId).FirstOrDefault(id => !request.SourcePaths.ContainsKey(id));
        if (missing is not null)
        {
            throw new ArgumentException($"No file for source '{missing}'.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory, nameof(request));
    }

    private static void Write(
        MaterializeRequest request,
        Dictionary<string, PdfDocument> documents,
        Stream output,
        IProgress<OperationProgress> progress,
        Action<PdfDocument, IReadOnlyList<PlacedPage>>? finish,
        CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        var baseDocument = documents[request.BaseSourceId];
        var basePageCount = baseDocument.PageCount;
        var foreignIds = documents.Keys.Where(id => id != request.BaseSourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in foreignIds)
        {
            ForeignPages.Prepare(documents[id]);
        }

        var placed = PageTreeBuilder.Build(
            baseDocument,
            request.BaseSourceId,
            plan,
            id => documents[id],
            out var removedPages,
            done => progress.Report(new OperationProgress(done, plan.Count, MaterializeStages.Pages)),
            cancellationToken);
        var pages = placed.Select(p => p.Page).ToArray();

        foreach (var (page, pageRef) in placed)
        {
            PageBoxes.ApplyEdits(page, pageRef);
        }

        AnnotationOwnership.EnsureUnique(baseDocument, pages);
        ForeignPages.Retarget(placed, foreignIds);
        DeadDestinations.Remove(baseDocument, removedPages, pages);
        if (!plan.IsIdentity(request.BaseSourceId, basePageCount))
        {
            // Labels ("i, ii, 1, 2…") describe the old page sequence; they would now be wrong.
            baseDocument.Internals.Catalog.Elements.Remove("/PageLabels");
        }

        baseDocument.Version = documents.Values.Max(d => d.Version);
        if (baseDocument.SecuritySettings.IsEncrypted)
        {
            // Only reached when a password was given (merging protected files): the output is never encrypted.
            baseDocument.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        }

        finish?.Invoke(baseDocument, placed);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new OperationProgress(0, 0, MaterializeStages.Write));
        try
        {
            baseDocument.Save(output, closeStream: false);
        }
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException or OutOfMemoryException))
        {
            throw new MaterializationException(MaterializationFailure.SourceUnreadable, request.BaseSourceId, ex);
        }
    }

    private async Task<PdfDocument> OpenAsync(string sourceId, string path, PdfDocumentOpenMode mode, string? password, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => Open(sourceId, path, mode, password), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            throw new MaterializationException(MaterializationFailure.SourceMissing, sourceId, ex);
        }
        catch (Exception ex) when (ex is not (MaterializationException or OperationCanceledException or OutOfMemoryException))
        {
            // PDFsharp is much stricter than PDFium: let PDFium write a clean copy and read that instead.
            string repaired;
            try
            {
                repaired = await repairer.RepairAsync(path, workingDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (PdfDocumentException repairError) when (repairError.Failure is PdfOpenFailure.PasswordRequired or PdfOpenFailure.WrongPassword)
            {
                throw new MaterializationException(MaterializationFailure.SourceEncrypted, sourceId, repairError);
            }
            catch (Exception repairError) when (repairError is not (OperationCanceledException or OutOfMemoryException))
            {
                throw new MaterializationException(MaterializationFailure.SourceUnreadable, sourceId, new AggregateException(ex, repairError));
            }

            try
            {
                return await Task.Run(() => Open(sourceId, repaired, mode, password: null), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryError) when (retryError is not (MaterializationException or OperationCanceledException or OutOfMemoryException))
            {
                throw new MaterializationException(MaterializationFailure.SourceUnreadable, sourceId, retryError);
            }
        }
    }

    private static PdfDocument Open(string sourceId, string path, PdfDocumentOpenMode mode, string? password)
    {
        var document = password is null ? PdfReader.Open(path, mode) : PdfReader.Open(path, password, mode);
        if (document.SecuritySettings.IsEncrypted && password is null)
        {
            document.Dispose();
            throw new MaterializationException(MaterializationFailure.SourceEncrypted, sourceId);
        }

        return document;
    }

    private static bool IsAccessFailure(Exception exception)
        => exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
            || (exception is IOException io && io.HResult is SharingViolation or LockViolation);
}

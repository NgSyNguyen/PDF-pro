using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Operations;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.TestSupport;

internal static class VirtualDocuments
{
    /// <summary>Every page of <paramref name="document"/> in order, as source "s0".</summary>
    public static VirtualDocument Of(IPdfDocument document)
        => VirtualDocument.Create(PagePlan.Identity(SourceIds.Original, document.PageCount, RandomPageKeys.Instance), Sources(document));

    /// <summary>A virtual document for <paramref name="plan"/> over source "s0" (and more sources if given).</summary>
    public static VirtualDocument Of(PagePlan plan, IPdfDocument document, params (string Id, IPdfDocument Document)[] others)
    {
        var sources = Sources(document);
        foreach (var (id, other) in others)
        {
            sources[id] = other;
        }

        return VirtualDocument.Create(plan, sources);
    }

    private static Dictionary<string, IPdfDocument> Sources(IPdfDocument document)
        => new(StringComparer.Ordinal) { [SourceIds.Original] = document };
}

internal static class TestOperations
{
    /// <summary>One PDFium thread for the operations that run through it (edit.content), shared by the tests.</summary>
    private static readonly Lazy<PdfiumGate> Gate = new(() => new PdfiumGate(NullLogger<PdfiumGate>.Instance));

    /// <summary>The page operations as the app registers them (their file-based execution is not used by view models).</summary>
    public static OperationCatalog Catalog()
    {
        var materializer = Substitute.For<Core.Organizing.IPlanMaterializer>();
        var loader = Substitute.For<IPdfDocumentLoader>();
        var repairer = Substitute.For<Operations.Materializing.ISourceRepairer>();
        return new OperationCatalog(
        [
            new RotatePagesOperation(materializer, loader),
            new MovePagesOperation(materializer, loader),
            new DeletePagesOperation(materializer, loader),
            new DuplicatePagesOperation(materializer, loader),
            new InsertBlankPagesOperation(materializer, loader),
            new InsertPagesOperation(materializer, loader),
            new CropPagesOperation(materializer, loader),
            new ExtractPagesOperation(materializer, loader),
            new ChangePageSizeOperation(),
            new MarkSplitOperation(materializer, loader),
            new SetSplitMarksOperation(materializer, loader),
            new InterleaveOperation(materializer, loader),
            new Operations.Splitting.SplitOperation(materializer, loader),
            new Operations.Layout.NUpOperation(repairer),
            new Operations.Layout.BookletOperation(repairer),
            new Operations.Layout.TileOperation(repairer),
            new Operations.Stamping.OverlayOperation(),
            new Operations.Stamping.WatermarkOperation(),
            new Operations.Stamping.BackgroundOperation(),
            new Operations.Stamping.HeaderFooterOperation(),
            new Operations.Stamping.PageNumbersOperation(),
            new Operations.Stamping.BatesOperation(),
            new Operations.Stamping.RemoveStampsOperation(),
            new Operations.Annotating.AddAnnotationsOperation(materializer, loader),
            new Operations.Annotating.UpdateAnnotationsOperation(materializer, loader),
            new Operations.Annotating.DeleteAnnotationsOperation(materializer, loader),
            new Operations.Annotating.EditFileAnnotationsOperation(),
            new Operations.Annotating.FlattenAnnotationsOperation(),
            new Operations.Editing.EditContentOperation(Gate.Value),
            new Operations.Forms.FillFormOperation(materializer, loader),
            new Operations.Forms.DesignFormOperation(),
            new Operations.Forms.MergeFormDataOperation(),
            new Operations.Forms.FlattenFormOperation(),
            new Operations.Forms.ResetFormOperation(materializer, loader),
            new Operations.Security.ProtectDocumentOperation(),
            new Operations.Security.UnprotectDocumentOperation(),
            new Operations.Security.SetProtectionOperation(materializer, loader),
            new Operations.Security.RedactOperation(Gate.Value),
            new Operations.Security.SanitizeOperation(Gate.Value),
        ]);
    }
}

/// <summary>
/// Real document workspaces over fake PDF documents: a real session store in a temporary folder (so session folders,
/// locks and manifests behave as in the app) and <see cref="FakePdfDocument"/> sources. Disposing closes every
/// workspace still open.
/// </summary>
internal sealed class WorkspaceFixture : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<DocumentWorkspace> _created = [];

    public WorkspaceFixture()
    {
        Paths = new AppPaths(_temp.Combine("data"), _temp.Combine("tools"));
        Paths.EnsureCreated();
        Store = new SessionStore(Paths, NullLogger<SessionStore>.Instance);
    }

    public AppPaths Paths { get; }

    public SessionStore Store { get; }

    /// <summary>The path of a folder in the test's temporary folder (not created).</summary>
    public string CreateFolder(string name) => _temp.Combine(name);

    /// <summary>A file that exists on disk.</summary>
    public string CreateFile(string fileName, byte[]? content = null)
    {
        var path = _temp.Combine(fileName);
        File.WriteAllBytes(path, content ?? [0x25, 0x50, 0x44, 0x46]);
        return path;
    }

    /// <summary>
    /// A workspace for <paramref name="document"/>. The session copies a scratch file, but records
    /// <paramref name="originalPath"/> as the file it was opened from (it need not exist).
    /// </summary>
    public async Task<DocumentWorkspace> CreateAsync(IPdfDocument document, string originalPath = @"C:\docs\a.pdf")
    {
        var scratch = CreateFile($"{Guid.NewGuid():N}.pdf");
        var folder = await Store.CreateAsync(scratch, TestContext.Current.CancellationToken);
        var source = new SessionSource(SourceIds.Original, SessionStore.SourceFileName(SourceIds.Original), SessionSourceKind.Original, document.PageCount, Path.GetFileName(originalPath));
        var session = new DocumentSession(
            folder.Id,
            folder.Directory,
            originalPath,
            [source],
            new SessionState(PagePlan.Identity(SourceIds.Original, document.PageCount, RandomPageKeys.Instance), SourceIds.Original, 0));
        var workspace = new DocumentWorkspace(
            session,
            Store,
            folder,
            [new OpenSource(source, folder.PathOf(source.FileName), document)],
            FileStamp.Read(originalPath),
            DateTimeOffset.UtcNow,
            NullLogger<DocumentWorkspace>.Instance);
        lock (_created)
        {
            _created.Add(workspace);
        }

        return workspace;
    }

    /// <summary>For workspace service substitutes: <c>Returns(_ =&gt; fixture.OpenAsync(document, path))</c>.</summary>
    public async Task<DocumentWorkspace?> OpenAsync(IPdfDocument document, string originalPath) => await CreateAsync(document, originalPath);

    public void Dispose()
    {
        lock (_created)
        {
            foreach (var workspace in _created)
            {
                workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        _temp.Dispose();
    }
}

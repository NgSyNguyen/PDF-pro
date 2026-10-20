using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.Tests.Documents;

public sealed class DocumentWorkspaceTests : IDisposable
{
    private readonly WorkspaceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task PendingSave_IsInTheManifestUntilCleared()
    {
        var workspace = await DirtyWorkspaceAsync(@"C:\docs\a.pdf");
        var pending = new PendingSaveDto(@"C:\docs\a.pdf", @"C:\docs\.a.pdf.1.tmp", @"C:\docs\.a.pdf.2.bak");

        await workspace.RecordPendingSaveAsync(pending);
        (await ReadManifestAsync(workspace)).PendingSave.ShouldBe(pending);

        await workspace.ClearPendingSaveAsync();
        (await ReadManifestAsync(workspace)).PendingSave.ShouldBeNull();
    }

    [Fact]
    public async Task HasChangedOnDisk_NoticesChangesByOtherPrograms()
    {
        var path = _fixture.CreateFile("a.pdf", [1, 2, 3, 4]);
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(2), path);
        workspace.HasChangedOnDisk().ShouldBeFalse();

        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5], TestContext.Current.CancellationToken);
        workspace.HasChangedOnDisk().ShouldBeTrue();

        File.Delete(path);
        workspace.HasChangedOnDisk().ShouldBeFalse("a missing file is simply written again");
    }

    [Fact]
    public async Task CompleteSave_ToAnotherFile_MakesItTheDocumentsFile()
    {
        var workspace = await DirtyWorkspaceAsync(@"C:\docs\a.pdf");
        var statusChanges = 0;
        workspace.StatusChanged += (_, _) => statusChanges++;

        workspace.CompleteSave(@"C:\docs\b.pdf", null);

        workspace.FilePath.ShouldBe(@"C:\docs\b.pdf");
        workspace.IsDirty.ShouldBeFalse();
        statusChanges.ShouldBeGreaterThan(0);
        var manifest = await ReadManifestAsync(workspace);
        manifest.OriginalPath.ShouldBe(@"C:\docs\b.pdf");
        manifest.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task CloseKeepingTheSession_LeavesItForRecovery()
    {
        var workspace = await DirtyWorkspaceAsync(@"C:\docs\a.pdf");
        var document = (FakePdfDocument)workspace.BaseDocument;

        await workspace.CloseAsync(keepSession: true);

        document.IsDisposed.ShouldBeTrue();
        var orphans = _fixture.Store.FindOrphans();
        try
        {
            var orphan = orphans.ShouldHaveSingleItem();
            orphan.Folder.Id.ShouldBe(workspace.Session.Id);
            orphan.Manifest.ShouldNotBeNull().IsDirty.ShouldBeTrue();
            orphan.Manifest.State.Plan[0].Rotation.ShouldBe(90);
        }
        finally
        {
            orphans.ToList().ForEach(o => _fixture.Store.Delete(o.Folder));
        }
    }

    private async Task<DocumentWorkspace> DirtyWorkspaceAsync(string originalPath)
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(3), originalPath);
        workspace.Apply(TestOperations.Catalog().Get<RotatePagesOperation>(), new RotatePagesParams("1", 90)).ShouldBeTrue();
        return workspace;
    }

    private static async Task<SessionManifest> ReadManifestAsync(DocumentWorkspace workspace)
    {
        await workspace.FlushManifestAsync();
        var json = await File.ReadAllTextAsync(Path.Combine(workspace.Session.Directory, SessionStore.ManifestFileName), TestContext.Current.CancellationToken);
        return SessionManifestSerializer.Deserialize(json);
    }
}

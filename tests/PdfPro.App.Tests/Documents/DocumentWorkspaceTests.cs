using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

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

    [Fact]
    public async Task AddSource_CopiesTheFileAndOpensTheCopy()
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(3));
        var file = _fixture.CreateFile("phụ lục.pdf", [7, 7, 7]);
        var loader = Substitute.For<IPdfDocumentLoader>();
        var inserted = FakePdfDocument.WithPages(2);
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(inserted);

        var source = await workspace.AddSourceAsync(file, loader, TestContext.Current.CancellationToken);

        source.Source.Id.ShouldBe("s1");
        source.Source.Kind.ShouldBe(SessionSourceKind.Inserted);
        source.Source.PageCount.ShouldBe(2);
        source.Source.DisplayName.ShouldBe("phụ lục.pdf");
        source.Document.ShouldBeSameAs(inserted);
        source.Path.ShouldBe(Path.Combine(workspace.Session.Directory, "sources", "s1.pdf"));
        (await File.ReadAllBytesAsync(source.Path, TestContext.Current.CancellationToken)).ShouldBe([7, 7, 7]);
        await loader.Received(1).LoadAsync(source.Path, null, Arg.Any<CancellationToken>());
        workspace.Sources.Count.ShouldBe(2);
        workspace.SourcePageSizes["s1"].Count.ShouldBe(2);
        workspace.IsDirty.ShouldBeFalse("adding a source changes nothing until its pages are inserted");
        (await ReadManifestAsync(workspace)).Sources.Select(s => s.Id).ShouldBe(["s0", "s1"]);

        workspace.Apply(TestOperations.Catalog().Get<InsertPagesOperation>(), new InsertPagesParams("s1", null, 4)).ShouldBeTrue();

        workspace.Pages.PageCount.ShouldBe(5);
        workspace.Pages.Pages[4].Source.ShouldBeSameAs(inserted);
    }

    [Fact]
    public async Task AddSource_TwoFiles_GetTheirOwnIds()
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(1));
        var loader = Substitute.For<IPdfDocumentLoader>();
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => FakePdfDocument.WithPages(1));

        var first = await workspace.AddSourceAsync(_fixture.CreateFile("a1.pdf"), loader, TestContext.Current.CancellationToken);
        var second = await workspace.AddSourceAsync(_fixture.CreateFile("a2.pdf"), loader, TestContext.Current.CancellationToken);

        (first.Source.Id, second.Source.Id).ShouldBe(("s1", "s2"));
    }

    [Fact]
    public async Task AddSource_EncryptedFile_IsRefusedAndItsCopyRemoved()
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(3));
        var loader = Substitute.For<IPdfDocumentLoader>();
        var encrypted = FakePdfDocument.WithPages(2);
        encrypted.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 4 };
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(encrypted);

        var error = await Should.ThrowAsync<PdfDocumentException>(() => workspace.AddSourceAsync(_fixture.CreateFile("locked.pdf"), loader, TestContext.Current.CancellationToken));

        error.Failure.ShouldBe(PdfOpenFailure.PasswordRequired);
        encrypted.IsDisposed.ShouldBeTrue();
        workspace.Sources.Count.ShouldBe(1);
        workspace.Session.Sources.Count.ShouldBe(1);
        File.Exists(Path.Combine(workspace.Session.Directory, "sources", "s1.pdf")).ShouldBeFalse();
    }

    [Fact]
    public async Task AddSource_UnreadableFile_LeavesNothingBehind()
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(3));
        var loader = Substitute.For<IPdfDocumentLoader>();
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PdfDocumentException(PdfOpenFailure.Corrupted, "Broken."));

        await Should.ThrowAsync<PdfDocumentException>(() => workspace.AddSourceAsync(_fixture.CreateFile("broken.pdf"), loader, TestContext.Current.CancellationToken));

        workspace.Sources.Count.ShouldBe(1);
        File.Exists(Path.Combine(workspace.Session.Directory, "sources", "s1.pdf")).ShouldBeFalse();
    }

    [Fact]
    public async Task AddSource_MissingFile_ThrowsAnIoError()
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(3));
        var missing = Path.Combine(Path.GetDirectoryName(_fixture.CreateFile("x.pdf"))!, "missing.pdf");

        await Should.ThrowAsync<FileNotFoundException>(() => workspace.AddSourceAsync(missing, Substitute.For<IPdfDocumentLoader>(), TestContext.Current.CancellationToken));

        workspace.Sources.Count.ShouldBe(1);
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

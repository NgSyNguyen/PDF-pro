using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Security;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Security;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Security;
using PdfPro.Operations.Signing;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.Tests.Documents;

public sealed class DocumentSaverTests : IDisposable
{
    private static readonly byte[] OldContent = Encoding.ASCII.GetBytes("%PDF-1.4 original");
    private static readonly byte[] NewContent = Encoding.ASCII.GetBytes("%PDF-1.7 saved by PDF_Pro");

    private readonly WorkspaceFixture _fixture = new();
    private readonly IPlanMaterializer _materializer = Substitute.For<IPlanMaterializer>();
    private readonly IPdfDocumentLoader _loader = Substitute.For<IPdfDocumentLoader>();
    private readonly IProgress<OperationProgress> _progress = Substitute.For<IProgress<OperationProgress>>();
    private readonly DocumentSaver _saver;
    private int _reopenedPages = 3;

    public DocumentSaverTests()
    {
        _saver = new DocumentSaver(_materializer, _loader, new ExtractPagesOperation(_materializer, _loader), NullLogger<DocumentSaver>.Instance);
        _materializer.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Stream>(1).WriteAsync(NewContent, call.ArgAt<CancellationToken>(3)).AsTask());
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IPdfDocument>(FakePdfDocument.WithPages(_reopenedPages)));
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Save_ReplacesTheFileAndMarksTheDocumentSaved()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        MaterializeRequest? request = null;
        _materializer.When(m => m.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()))
            .Do(call => request = call.ArgAt<MaterializeRequest>(0));

        await _saver.SaveAsync(workspace, path, _progress, TestToken);

        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(NewContent);
        workspace.IsDirty.ShouldBeFalse();
        workspace.IsBusy.ShouldBeFalse();
        workspace.OriginalStamp.ShouldBe(FileStamp.Read(path));
        workspace.HasChangedOnDisk().ShouldBeFalse();
        LeftoversNextTo(path).ShouldBeEmpty();

        request.ShouldNotBeNull();
        request.Plan.ShouldBeSameAs(workspace.Session.Plan);
        request.BaseSourceId.ShouldBe(SourceIds.Original);
        request.SourcePaths[SourceIds.Original].ShouldStartWith(workspace.Session.Directory);
        request.WorkingDirectory.ShouldBe(workspace.WorkDirectory);
        await _loader.Received(1).LoadAsync(Arg.Is<string>(p => p != path), Arg.Is<string?>(p => p == null), Arg.Any<CancellationToken>());

        var manifest = await ReadManifestAsync(workspace);
        manifest.IsDirty.ShouldBeFalse();
        manifest.PendingSave.ShouldBeNull();
    }

    [Fact]
    public async Task Save_RecordsItsFilesInTheManifestBeforeWriting()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        var manifestPath = Path.Combine(workspace.Session.Directory, SessionStore.ManifestFileName);
        PendingSaveDto? recorded = null;
        _materializer.When(m => m.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()))
            .Do(_ => recorded = SessionManifestSerializer.Deserialize(File.ReadAllText(manifestPath)).PendingSave);

        await _saver.SaveAsync(workspace, path, _progress, TestToken);

        recorded.ShouldNotBeNull("a crash while writing must be repairable");
        recorded.TargetPath.ShouldBe(path);
        Path.GetDirectoryName(recorded.TempPath).ShouldBe(Path.GetDirectoryName(path));
        recorded.BackupPath.ShouldNotBeNull();
        (await ReadManifestAsync(workspace)).PendingSave.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAs_NewFile_BecomesTheDocumentsFile()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        var target = Path.Combine(Path.GetDirectoryName(path)!, "Bản sao.pdf");

        await _saver.SaveAsync(workspace, target, _progress, TestToken);

        (await File.ReadAllBytesAsync(target, TestToken)).ShouldBe(NewContent);
        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
        workspace.FilePath.ShouldBe(target);
        workspace.FileName.ShouldBe("Bản sao.pdf");
        workspace.IsDirty.ShouldBeFalse();
        (await ReadManifestAsync(workspace)).OriginalPath.ShouldBe(target);
    }

    [Fact]
    public async Task Save_TargetLockedByAnotherProgram_FailsAsLockedAndKeepsTheFile()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);

        DocumentSaveException error;
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            error = await Should.ThrowAsync<DocumentSaveException>(() => _saver.SaveAsync(workspace, path, _progress, TestToken));
        }

        error.Failure.ShouldBe(SaveFailure.Locked);
        error.Message.ShouldNotContain(path);
        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
        workspace.IsDirty.ShouldBeTrue();
        workspace.IsBusy.ShouldBeFalse();
        LeftoversNextTo(path).ShouldBeEmpty();
        (await ReadManifestAsync(workspace)).PendingSave.ShouldBeNull();
    }

    [Fact]
    public async Task Save_WrittenFileWithWrongPageCount_IsNotUsed()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        _reopenedPages = 2;

        var error = await Should.ThrowAsync<DocumentSaveException>(() => _saver.SaveAsync(workspace, path, _progress, TestToken));

        error.Failure.ShouldBe(SaveFailure.VerificationFailed);
        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
        LeftoversNextTo(path).ShouldBeEmpty();
        workspace.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task Save_WrittenFileThatDoesNotOpen_IsNotUsed()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PdfDocumentException(PdfOpenFailure.Corrupted, "broken"));

        var error = await Should.ThrowAsync<DocumentSaveException>(() => _saver.SaveAsync(workspace, path, _progress, TestToken));

        error.Failure.ShouldBe(SaveFailure.VerificationFailed);
        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
    }

    [Fact]
    public async Task Save_SourceThatCannotBeRead_FailsAsSourceUnreadable()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        _materializer.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new MaterializationException(MaterializationFailure.SourceUnreadable, SourceIds.Original));

        var error = await Should.ThrowAsync<DocumentSaveException>(() => _saver.SaveAsync(workspace, path, _progress, TestToken));

        error.Failure.ShouldBe(SaveFailure.SourceUnreadable);
        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
        LeftoversNextTo(path).ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_Cancelled_KeepsTheFileAndTheChanges()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        using var cancellation = new CancellationTokenSource();
        _materializer.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await cancellation.CancelAsync();
                call.ArgAt<CancellationToken>(3).ThrowIfCancellationRequested();
            });

        await Should.ThrowAsync<OperationCanceledException>(() => _saver.SaveAsync(workspace, path, _progress, cancellation.Token));

        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
        LeftoversNextTo(path).ShouldBeEmpty();
        workspace.IsDirty.ShouldBeTrue();
        workspace.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Save_BlocksEditsWhileWriting()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        bool? canEditWhileWriting = null;
        _materializer.When(m => m.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()))
            .Do(_ => canEditWhileWriting = workspace.CanEdit);

        await _saver.SaveAsync(workspace, path, _progress, TestToken);

        canEditWhileWriting.ShouldBe(false);
        workspace.CanEdit.ShouldBeTrue();
    }

    [Fact]
    public async Task Save_PasswordProtectedDocument_IsRefused()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var document = FakePdfDocument.WithPages(3);
        document.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 4 };
        var workspace = await _fixture.CreateAsync(document, path);

        await Should.ThrowAsync<InvalidOperationException>(() => _saver.SaveAsync(workspace, path, _progress, TestToken));

        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
    }

    [Fact]
    public async Task Export_WritesTheChosenPagesInDocumentOrderAndLeavesTheDocumentAlone()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        var target = Path.Combine(Path.GetDirectoryName(path)!, "a (trang 1, 3).pdf");
        MaterializeRequest? request = null;
        bool? canEditWhileWriting = null;
        _materializer.When(m => m.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                request = call.ArgAt<MaterializeRequest>(0);
                canEditWhileWriting = workspace.CanEdit;
            });
        _reopenedPages = 2;

        await _saver.ExportAsync(workspace, [2, 0], target, _progress, TestToken);

        (await File.ReadAllBytesAsync(target, TestToken)).ShouldBe(NewContent);
        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
        request.ShouldNotBeNull();
        request.Plan.Select(p => p.PageKey).ShouldBe([workspace.Session.Plan[0].PageKey, workspace.Session.Plan[2].PageKey]);
        request.Plan[0].RotationDelta.ShouldBe(90, "pages are written as the document shows them");
        request.BaseSourceId.ShouldBe(SourceIds.Original);
        canEditWhileWriting.ShouldBe(true);
        workspace.IsDirty.ShouldBeTrue();
        workspace.FilePath.ShouldBe(path);
        LeftoversNextTo(target).ShouldBeEmpty();
        (await ReadManifestAsync(workspace)).PendingSave.ShouldBeNull();
    }

    [Fact]
    public async Task Export_IntoTheDocumentsOwnFile_IsRefused()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);

        await Should.ThrowAsync<ArgumentException>(() => _saver.ExportAsync(workspace, [0], path.ToUpperInvariant(), _progress, TestToken));

        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
    }

    [Fact]
    public async Task Export_Failure_KeepsAnExistingTarget()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        var target = _fixture.CreateFile("old-extract.pdf", OldContent);
        _reopenedPages = 5;

        var error = await Should.ThrowAsync<DocumentSaveException>(() => _saver.ExportAsync(workspace, [0], target, _progress, TestToken));

        error.Failure.ShouldBe(SaveFailure.VerificationFailed);
        (await File.ReadAllBytesAsync(target, TestToken)).ShouldBe(OldContent);
        LeftoversNextTo(target).ShouldBeEmpty();
    }

    [Fact]
    public async Task Export_PagesThatDoNotExist_AreRefused()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);

        await Should.ThrowAsync<Core.Operations.OperationValidationException>(
            () => _saver.ExportAsync(workspace, [7], Path.Combine(Path.GetDirectoryName(path)!, "x.pdf"), _progress, TestToken));
    }

    [Fact]
    public async Task Save_OfAProtectedDocument_EncryptsItWithItsPasswords_AndChecksItWithTheOpenPassword()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        Protect(workspace, new DocumentPasswords("mở-ra", "chủ-nhân"), DocumentPermissions.Print);
        MaterializeRequest? request = null;
        _materializer.When(m => m.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()))
            .Do(call => request = call.ArgAt<MaterializeRequest>(0));
        _loader.LoadAsync(Arg.Any<string>(), "mở-ra", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IPdfDocument>(ProtectedFake(3)));

        await _saver.SaveAsync(workspace, path, _progress, TestToken);

        request.ShouldNotBeNull().Security.ShouldBe(new DocumentSecurity("mở-ra", "chủ-nhân", EncryptionAlgorithm.Aes256, DocumentPermissions.Print));
        request.ToString().ShouldNotContain("chủ-nhân", Case.Sensitive, "a request never shows the passwords");
        await _loader.Received(1).LoadAsync(Arg.Is<string>(p => p != path), "mở-ra", Arg.Any<CancellationToken>());
        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(NewContent);
        workspace.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task Save_WhenTheWrittenFileIsNotProtected_KeepsTheOriginal()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        Protect(workspace, new DocumentPasswords(null, "chủ-nhân"), DocumentPermissions.None);

        var error = await Should.ThrowAsync<DocumentSaveException>(() => _saver.SaveAsync(workspace, path, _progress, TestToken));

        error.Failure.ShouldBe(SaveFailure.VerificationFailed);
        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
        LeftoversNextTo(path).ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_OfAProtectionWithoutItsPasswords_IsRefused_NeverWrittenUnprotected()
    {
        var path = _fixture.CreateFile("a.pdf", OldContent);
        var workspace = await DirtyWorkspaceAsync(path);
        var unknown = new ProtectionSetting(Guid.NewGuid(), EncryptionAlgorithm.Aes256, DocumentPermissions.All, HasOpenPassword: true);
        workspace.Apply(TestOperations.Catalog().Get<SetProtectionOperation>(), new SetProtectionParams(unknown)).ShouldBeTrue();
        workspace.NeedsPasswordsToSave.ShouldBeTrue("a recovered session knows the protection but not its passwords");

        await Should.ThrowAsync<InvalidOperationException>(() => _saver.SaveAsync(workspace, path, _progress, TestToken));

        await _materializer.DidNotReceiveWithAnyArgs().MaterializeAsync(default!, default!, default!, TestToken);
        (await File.ReadAllBytesAsync(path, TestToken)).ShouldBe(OldContent);
    }

    private static void Protect(DocumentWorkspace workspace, DocumentPasswords passwords, DocumentPermissions permissions)
    {
        var setting = new ProtectionSetting(workspace.Passwords.Add(passwords), EncryptionAlgorithm.Aes256, permissions, passwords.Open is not null);
        workspace.Apply(TestOperations.Catalog().Get<SetProtectionOperation>(), new SetProtectionParams(setting)).ShouldBeTrue();
        workspace.NeedsPasswordsToSave.ShouldBeFalse();
    }

    private static FakePdfDocument ProtectedFake(int pages)
    {
        var document = FakePdfDocument.WithPages(pages);
        document.Features = new PdfDocumentFeatures("2.0", 6, 0xFFFFF0C4, 0, PdfFormType.None, false, 0xFFFFF0C4);
        return document;
    }

    [Fact]
    public async Task Save_CommentsOnASignedFile_AreAppended_SoItsSignatureStays()
    {
        var (workspace, path, signed) = await SignedWorkspaceAsync();
        workspace.Apply(TestOperations.Catalog().Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(1, [Note()])).ShouldBeTrue();
        _saver.KeepsSignatures(workspace).ShouldBeTrue();

        await _saver.SaveAsync(workspace, path, _progress, TestToken);

        var saved = await File.ReadAllBytesAsync(path, TestToken);
        saved.Length.ShouldBeGreaterThan(signed.Length);
        saved.AsSpan(0, signed.Length).SequenceEqual(signed).ShouldBeTrue("the signed file is untouched before the update");
        await _materializer.DidNotReceiveWithAnyArgs().MaterializeAsync(default!, default!, default!, TestToken);
        using var read = PdfReader.Open(new MemoryStream(saved), PdfDocumentOpenMode.Import);
        FormReader.Read(read).ShouldHaveSingleItem().IsSigned.ShouldBeTrue();
        read.Pages[0].Elements.GetArray("/Annots").ShouldNotBeNull().Elements.OfType<PdfReference>()
            .Select(r => ((PdfDictionary)r.Value).Elements.GetName("/Subtype")).ShouldContain("/Text");
        workspace.IsDirty.ShouldBeFalse();
        LeftoversNextTo(path).ShouldBeEmpty();
    }

    [Fact]
    public async Task KeepsSignatures_OnlyWhenCommentsAreTheOnlyChange_OfASignedFile()
    {
        var (workspace, _, _) = await SignedWorkspaceAsync();
        _saver.KeepsSignatures(workspace).ShouldBeFalse("nothing changed");

        workspace.Apply(TestOperations.Catalog().Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(1, [Note()])).ShouldBeTrue();
        _saver.KeepsSignatures(workspace).ShouldBeTrue();

        workspace.Apply(TestOperations.Catalog().Get<RotatePagesOperation>(), new RotatePagesParams("2", 90)).ShouldBeTrue();
        _saver.KeepsSignatures(workspace).ShouldBeFalse("a turned page is written anew");

        var unsigned = await _fixture.CreateAsync(FakePdfDocument.WithPages(6), _fixture.CreateFile("b.pdf"));
        unsigned.Apply(TestOperations.Catalog().Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(1, [Note()])).ShouldBeTrue();
        _saver.KeepsSignatures(unsigned).ShouldBeFalse("only signed files are updated this way");
    }

    private static NoteAnnotation Note()
        => new("n1", new RgbColor(255, 230, 0), 1, "Lan", DateTimeOffset.Now, DateTimeOffset.Now, "Đã xem", new PdfPoint(100, 700));

    /// <summary>An open document whose file is a real signed PDF (six pages), the session holding the same bytes.</summary>
    private async Task<(DocumentWorkspace Workspace, string Path, byte[] Signed)> SignedWorkspaceAsync()
    {
        using var certificate = SigningCertificates.Signer();
        var signed = await PdfSigner.SignAsync(await File.ReadAllBytesAsync(Samples.Basic, TestToken), new SignRequest("ChuKy1"), certificate, null, DateTimeOffset.Now, TestToken);
        var path = _fixture.CreateFile("đã ký.pdf", signed);
        var document = FakePdfDocument.WithPages(6);
        document.Features = FakePdfDocument.PlainFeatures with { SignatureCount = 1 };
        _reopenedPages = 6;
        return (await _fixture.CreateAsync(document, path, signed), path, signed);
    }

    private async Task<DocumentWorkspace> DirtyWorkspaceAsync(string path)
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(3), path);
        workspace.Apply(TestOperations.Catalog().Get<RotatePagesOperation>(), new RotatePagesParams("1", 90)).ShouldBeTrue();
        workspace.IsDirty.ShouldBeTrue();
        return workspace;
    }

    /// <summary>Temporary or backup files of a save (".a.pdf.{guid}.tmp/.bak").</summary>
    private static string[] LeftoversNextTo(string path)
        => Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.*");

    private static async Task<SessionManifest> ReadManifestAsync(DocumentWorkspace workspace)
    {
        await workspace.FlushManifestAsync();
        var json = await File.ReadAllTextAsync(Path.Combine(workspace.Session.Directory, SessionStore.ManifestFileName), TestContext.Current.CancellationToken);
        return SessionManifestSerializer.Deserialize(json);
    }
}

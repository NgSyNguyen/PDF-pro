using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Documents;

public sealed class RecoveryServiceTests : IDisposable
{
    private readonly WorkspaceFixture _fixture = new();
    private readonly IPdfDocumentLoader _loader = Substitute.For<IPdfDocumentLoader>();
    private readonly IRecoveryDialog _dialog = Substitute.For<IRecoveryDialog>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly RecoveryService _service;

    public RecoveryServiceTests()
    {
        _service = new RecoveryService(_fixture.Store, _loader, TestOperations.Catalog(), _dialog, _messages, NullLoggerFactory.Instance);
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IPdfDocument>(FakePdfDocument.WithPages(3)));
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Recover_NothingLeftBehind_AsksNothing()
    {
        (await _service.RecoverAsync(TestToken)).ShouldBeEmpty();

        _dialog.DidNotReceiveWithAnyArgs().Ask(default!);
    }

    [Fact]
    public async Task Recover_SessionsWithoutUnsavedChangesOrManifest_AreRemovedSilently()
    {
        await CreateOrphanAsync(@"C:\docs\clean.pdf", dirty: false);
        var noManifest = await _fixture.Store.CreateAsync(_fixture.CreateFile("x.pdf"), TestToken);
        noManifest.Dispose();

        (await _service.RecoverAsync(TestToken)).ShouldBeEmpty();

        _dialog.DidNotReceiveWithAnyArgs().Ask(default!);
        Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Recover_Restore_ReopensTheDocumentWithItsHistory()
    {
        var id = await CreateOrphanAsync(@"C:\docs\Báo cáo.pdf");
        RestoreEverything();

        var workspace = (await _service.RecoverAsync(TestToken)).ShouldHaveSingleItem();

        await using (workspace)
        {
            workspace.Session.Id.ShouldBe(id);
            workspace.FilePath.ShouldBe(@"C:\docs\Báo cáo.pdf");
            workspace.IsDirty.ShouldBeTrue();
            workspace.Pages.PageCount.ShouldBe(3);
            workspace.Session.Plan[0].RotationDelta.ShouldBe(90);
            workspace.Session.History.UndoCount.ShouldBe(1);
            _fixture.Store.FindOrphans().ShouldBeEmpty("the restored session is locked again");

            workspace.Undo();
            workspace.IsDirty.ShouldBeFalse("the undo history and save point came back too");
        }

        _dialog.Received(1).Ask(Arg.Is<IReadOnlyList<RecoveryItem>>(items =>
            items.Count == 1 && items[0].SessionId == id && items[0].FileName == "Báo cáo.pdf" && items[0].Folder == @"C:\docs" && items[0].Changes == 1));
        Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Recover_Later_KeepsTheSessionsForTheNextStart()
    {
        var id = await CreateOrphanAsync(@"C:\docs\a.pdf");
        _dialog.Ask(Arg.Any<IReadOnlyList<RecoveryItem>>()).Returns(RecoveryDecision.Later);

        (await _service.RecoverAsync(TestToken)).ShouldBeEmpty();

        TakeOrphanIds().ShouldBe([id]);
    }

    [Fact]
    public async Task Recover_DiscardAll_RemovesTheSessions()
    {
        await CreateOrphanAsync(@"C:\docs\a.pdf");
        await CreateOrphanAsync(@"C:\docs\b.pdf");
        _dialog.Ask(Arg.Any<IReadOnlyList<RecoveryItem>>()).Returns(new RecoveryDecision(RecoveryAction.DiscardAll, new HashSet<Guid>()));

        (await _service.RecoverAsync(TestToken)).ShouldBeEmpty();

        Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Recover_UncheckedDocuments_AreDiscarded()
    {
        var kept = await CreateOrphanAsync(@"C:\docs\a.pdf");
        await CreateOrphanAsync(@"C:\docs\b.pdf");
        _dialog.Ask(Arg.Any<IReadOnlyList<RecoveryItem>>()).Returns(new RecoveryDecision(RecoveryAction.Restore, new HashSet<Guid> { kept }));

        var workspace = (await _service.RecoverAsync(TestToken)).ShouldHaveSingleItem();

        await using (workspace)
        {
            workspace.Session.Id.ShouldBe(kept);
            Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldHaveSingleItem().ShouldBe(workspace.Session.Directory);
        }
    }

    [Fact]
    public async Task Recover_SaveInterruptedDuringTheSwap_PutsThePreviousFileBack()
    {
        var backup = _fixture.CreateFile(".doc.pdf.1.bak", Encoding.ASCII.GetBytes("previous"));
        var temp = _fixture.CreateFile(".doc.pdf.2.tmp", Encoding.ASCII.GetBytes("new"));
        var target = Path.Combine(Path.GetDirectoryName(backup)!, "doc.pdf"); // moved aside, not replaced yet
        await CreateOrphanAsync(target, pending: new PendingSaveDto(target, temp, backup));
        _dialog.Ask(Arg.Any<IReadOnlyList<RecoveryItem>>()).Returns(RecoveryDecision.Later);

        await _service.RecoverAsync(TestToken);

        (await File.ReadAllTextAsync(target, TestToken)).ShouldBe("previous");
        File.Exists(temp).ShouldBeFalse();
        File.Exists(backup).ShouldBeFalse();
        TakeOrphanIds().Count.ShouldBe(1, "the unsaved changes are still offered");
    }

    [Fact]
    public async Task Recover_SourceThatCannotBeOpened_KeepsTheSessionAndExplains()
    {
        var id = await CreateOrphanAsync(@"C:\docs\a.pdf");
        RestoreEverything();
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PdfDocumentException(PdfOpenFailure.Corrupted, "broken"));

        (await _service.RecoverAsync(TestToken)).ShouldBeEmpty();

        await _messages.Received(1).ShowErrorAsync(Strings.Recovery_Title, string.Format(CultureInfo.CurrentCulture, Strings.Recovery_Failed, "a.pdf"));
        TakeOrphanIds().ShouldBe([id]);
    }

    [Fact]
    public async Task Recover_HistoryStepFromAnotherVersion_RestoresTheCurrentState()
    {
        await CreateOrphanAsync(@"C:\docs\a.pdf");
        var orphan = _fixture.Store.FindOrphans().ShouldHaveSingleItem();
        var manifest = orphan.Manifest.ShouldNotBeNull();
        await _fixture.Store.WriteManifestAsync(orphan.Folder, manifest with { Undo = [manifest.Undo[0] with { Operation = "future.feature" }] }, TestToken);
        orphan.Folder.Dispose();
        RestoreEverything();

        var workspace = (await _service.RecoverAsync(TestToken)).ShouldHaveSingleItem();

        await using (workspace)
        {
            workspace.Session.Plan[0].RotationDelta.ShouldBe(90);
            workspace.Session.History.CanUndo.ShouldBeFalse();
            workspace.IsDirty.ShouldBeTrue();
        }
    }

    /// <summary>A session left behind with one rotation (or none), as after a crash.</summary>
    private async Task<Guid> CreateOrphanAsync(string originalPath, bool dirty = true, PendingSaveDto? pending = null)
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(3), originalPath);
        if (dirty)
        {
            workspace.Apply(TestOperations.Catalog().Get<RotatePagesOperation>(), new RotatePagesParams("1", 90)).ShouldBeTrue();
        }

        if (pending is not null)
        {
            await workspace.RecordPendingSaveAsync(pending);
        }

        await workspace.CloseAsync(keepSession: true);
        return workspace.Session.Id;
    }

    private void RestoreEverything()
        => _dialog.Ask(Arg.Any<IReadOnlyList<RecoveryItem>>())
            .Returns(call => new RecoveryDecision(RecoveryAction.Restore, call.Arg<IReadOnlyList<RecoveryItem>>().Select(i => i.SessionId).ToHashSet()));

    /// <summary>The sessions a next start would find (their locks are released again).</summary>
    private List<Guid> TakeOrphanIds()
    {
        var orphans = _fixture.Store.FindOrphans();
        orphans.ToList().ForEach(o => o.Folder.Dispose());
        return [.. orphans.Select(o => o.Folder.Id)];
    }
}

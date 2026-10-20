using System.Globalization;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Printing;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Sessions;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>Saving, closing with unsaved changes, exit and crash recovery.</summary>
public sealed partial class MainWindowViewModelTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Save_IsEnabledOnlyForAnEditableDocumentWithChanges()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        viewModel.SaveCommand.CanExecute(null).ShouldBeFalse("nothing changed yet");
        viewModel.SaveAsCommand.CanExecute(null).ShouldBeTrue();

        viewModel.RotateRightCommand.Execute(null);
        viewModel.SaveCommand.CanExecute(null).ShouldBeTrue();

        viewModel.SelectedTab = viewModel.Home;
        viewModel.SaveCommand.CanExecute(null).ShouldBeFalse();
        viewModel.SaveAsCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Save_PasswordProtectedDocument_IsDisabled()
    {
        var document = FakePdfDocument.WithPages(2);
        document.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 4 };

        var (viewModel, _) = await CreateWithFileAsync("a.pdf", document);

        viewModel.SaveCommand.CanExecute(null).ShouldBeFalse();
        viewModel.SaveAsCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Save_WritesOverTheDocumentsFileWithProgressInTheStatusBar()
    {
        var (viewModel, path) = await CreateDirtyAsync();
        var saving = new TaskCompletionSource();
        AnySave().Returns(saving.Task);

        var command = viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.StatusBar.IsTaskRunning.ShouldBeTrue();
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Save_Progress, "a.pdf"));
        viewModel.SaveAsCommand.CanExecute(null).ShouldBeFalse("one task at a time");
        saving.SetResult();
        await command;

        await _saver.Received(1).SaveAsync(viewModel.ActiveDocument!.Workspace, path, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Save_Done, "a.pdf"));
        viewModel.StatusBar.IsTaskRunning.ShouldBeFalse();
        _fileDialogs.DidNotReceiveWithAnyArgs().PickSavePath(default!, default);
        await _messages.DidNotReceiveWithAnyArgs().AskAsync(default!, default!, default!, default, default!);
    }

    [Fact]
    public async Task SaveAs_SavesToTheChosenFileAndRemembersIt()
    {
        var (viewModel, path) = await CreateDirtyAsync();
        var target = Path.Combine(Path.GetDirectoryName(path)!, "copy.pdf");
        _fileDialogs.PickSavePath("a.pdf", Path.GetDirectoryName(path)).Returns(target);

        await viewModel.SaveAsCommand.ExecuteAsync(null);

        await _saver.Received(1).SaveAsync(Arg.Any<DocumentWorkspace>(), target, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        _settings.Current.RecentFiles[0].ShouldBe(target);
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Save_Done, "copy.pdf"));
    }

    [Fact]
    public async Task SaveAs_CancelledDialog_SavesNothing()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns((string?)null);

        await viewModel.SaveAsCommand.ExecuteAsync(null);

        await DidNotSave();
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Status_Ready);
    }

    [Fact]
    public async Task SaveAs_OverAFileOpenInAnotherTab_IsRefused()
    {
        var (viewModel, path) = await CreateDirtyAsync();
        var other = _fixture.CreateFile("b.pdf");
        await viewModel.OpenFilesAsync([other]);
        viewModel.SelectedTab = viewModel.Documents.First();
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(other);

        await viewModel.SaveAsCommand.ExecuteAsync(null);

        await DidNotSave();
        await _messages.Received(1).ShowErrorAsync(Strings.Save_Error_Title, Format(Strings.Save_AlreadyOpen, "b.pdf"));
        viewModel.ActiveDocument!.FilePath.ShouldBe(path);
    }

    [Fact]
    public async Task Save_SignedDocument_OffersSaveAsFirst()
    {
        var document = FakePdfDocument.WithPages(3);
        document.Features = FakePdfDocument.PlainFeatures with { SignatureCount = 1 };
        var (viewModel, path) = await CreateDirtyAsync(document);
        var target = Path.Combine(Path.GetDirectoryName(path)!, "unsigned.pdf");
        AskSigned().Returns(DialogChoice.Primary);
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(target);

        await viewModel.SaveCommand.ExecuteAsync(null);

        await _saver.Received(1).SaveAsync(Arg.Any<DocumentWorkspace>(), target, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DialogChoice.Secondary, 1)]
    [InlineData(DialogChoice.Cancel, 0)]
    public async Task Save_SignedDocument_OverwritesOnlyWhenTheUserInsists(DialogChoice choice, int saves)
    {
        var document = FakePdfDocument.WithPages(3);
        document.Features = FakePdfDocument.PlainFeatures with { SignatureCount = 2 };
        var (viewModel, path) = await CreateDirtyAsync(document);
        AskSigned().Returns(choice);

        await viewModel.SaveCommand.ExecuteAsync(null);

        await _saver.Received(saves).SaveAsync(Arg.Any<DocumentWorkspace>(), path, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        _fileDialogs.DidNotReceiveWithAnyArgs().PickSavePath(default!, default);
    }

    [Fact]
    public async Task Save_FileChangedByAnotherProgram_WarnsBeforeOverwriting()
    {
        var (viewModel, path) = await CreateDirtyAsync();
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7]);
        _messages.AskAsync(
            Strings.Save_Changed_Title,
            Format(Strings.Save_Changed_Message, "a.pdf"),
            Strings.Save_SaveAsButton,
            Strings.Save_OverwriteAnyway,
            Strings.Common_Cancel).Returns(DialogChoice.Cancel);

        await viewModel.SaveCommand.ExecuteAsync(null);

        await DidNotSave();
        viewModel.ActiveDocument!.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task Save_FolderNoLongerExists_AsksWhereToSave()
    {
        var missing = Path.Combine(Path.GetTempPath(), "PdfPro.Tests", Guid.NewGuid().ToString("N"), "a.pdf");
        _workspaces.OpenAsync(missing, Arg.Any<CancellationToken>()).Returns(_ => _fixture.OpenAsync(FakePdfDocument.WithPages(2), missing));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([missing]);
        viewModel.RotateRightCommand.Execute(null);
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns((string?)null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        _fileDialogs.Received(1).PickSavePath("a.pdf", Arg.Any<string?>());
        await DidNotSave();
    }

    [Fact]
    public async Task Save_LockedFile_ExplainsAndOffersSaveAs()
    {
        var (viewModel, path) = await CreateDirtyAsync();
        var target = Path.Combine(Path.GetDirectoryName(path)!, "copy.pdf");
        _saver.SaveAsync(Arg.Any<DocumentWorkspace>(), path, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DocumentSaveException(SaveFailure.Locked));
        _messages.AskAsync(Strings.Save_Error_Title, Format(Strings.Save_Error_Locked, "a.pdf"), Strings.Save_SaveAsButton, null, Strings.Common_Close)
            .Returns(DialogChoice.Primary);
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(target);

        await viewModel.SaveCommand.ExecuteAsync(null);

        await _saver.Received(1).SaveAsync(Arg.Any<DocumentWorkspace>(), target, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Save_Done, "copy.pdf"));
    }

    [Theory]
    [InlineData(SaveFailure.DiskFull)]
    [InlineData(SaveFailure.SourceUnreadable)]
    [InlineData(SaveFailure.VerificationFailed)]
    [InlineData(SaveFailure.Failed)]
    public async Task Save_Failure_IsExplainedAndTheChangesAreKept(SaveFailure failure)
    {
        var (viewModel, _) = await CreateDirtyAsync();
        AnySave().ThrowsAsync(new DocumentSaveException(failure));
        var expected = failure switch
        {
            SaveFailure.DiskFull => Strings.Save_Error_DiskFull,
            SaveFailure.SourceUnreadable => Strings.Save_Error_SourceUnreadable,
            SaveFailure.VerificationFailed => Strings.Save_Error_Verification,
            _ => Strings.Save_Error_Failed,
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Save_Error_Title, Format(expected, "a.pdf"));
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Save_FailedStatus);
        viewModel.StatusBar.IsTaskRunning.ShouldBeFalse();
        viewModel.ActiveDocument!.IsDirty.ShouldBeTrue();
        viewModel.SaveCommand.CanExecute(null).ShouldBeTrue("the user can try again");
    }

    [Fact]
    public async Task Save_CancelButton_CancelsTheSave()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        var token = CancellationToken.None;
        var saving = new TaskCompletionSource();
        AnySave().Returns(call =>
        {
            token = call.ArgAt<CancellationToken>(3);
            return saving.Task;
        });

        var command = viewModel.SaveCommand.ExecuteAsync(null);
        viewModel.StatusBar.CancelTaskCommand.Execute(null);

        token.IsCancellationRequested.ShouldBeTrue();
        saving.SetCanceled(token);
        await command;
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Save_Cancelled);
        viewModel.ActiveDocument!.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task CloseTab_UnsavedChanges_SaveThenClose()
    {
        var (viewModel, path) = await CreateDirtyAsync();
        var tab = viewModel.ActiveDocument!;
        var sessionFolder = tab.Workspace.Session.Directory;
        SaverMarksSaved();
        AskSaveChanges("a.pdf").Returns(DialogChoice.Primary);

        await viewModel.CloseTabCommand.ExecuteAsync(tab);

        await _saver.Received(1).SaveAsync(tab.Workspace, path, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        viewModel.Documents.ShouldBeEmpty();
        Directory.Exists(sessionFolder).ShouldBeFalse("a saved document leaves nothing to recover");
    }

    [Fact]
    public async Task CloseTab_DontSave_ClosesAndRemovesTheSession()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        var tab = viewModel.ActiveDocument!;
        var sessionFolder = tab.Workspace.Session.Directory;
        AskSaveChanges("a.pdf").Returns(DialogChoice.Secondary);

        await viewModel.CloseTabCommand.ExecuteAsync(tab);

        await DidNotSave();
        viewModel.Documents.ShouldBeEmpty();
        Directory.Exists(sessionFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task CloseTab_Cancel_KeepsTheDocumentOpen()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        var tab = viewModel.ActiveDocument!;
        AskSaveChanges("a.pdf").Returns(DialogChoice.Cancel);

        await viewModel.CloseTabCommand.ExecuteAsync(tab);

        viewModel.Documents.ShouldHaveSingleItem().ShouldBeSameAs(tab);
        tab.IsDirty.ShouldBeTrue();
        tab.DiscardChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task CloseTab_SaveFails_KeepsTheDocumentOpen()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        var tab = viewModel.ActiveDocument!;
        AskSaveChanges("a.pdf").Returns(DialogChoice.Primary);
        AnySave().ThrowsAsync(new DocumentSaveException(SaveFailure.DiskFull));

        await viewModel.CloseTabCommand.ExecuteAsync(tab);

        viewModel.Documents.ShouldHaveSingleItem().ShouldBeSameAs(tab);
        await _messages.Received(1).ShowErrorAsync(Strings.Save_Error_Title, Format(Strings.Save_Error_DiskFull, "a.pdf"));
    }

    [Fact]
    public async Task CloseTab_DocumentWithoutChanges_ClosesWithoutAsking()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");

        await viewModel.CloseTabCommand.ExecuteAsync(viewModel.ActiveDocument);

        viewModel.Documents.ShouldBeEmpty();
        await _messages.DidNotReceiveWithAnyArgs().AskAsync(default!, default!, default!, default, default!);
    }

    [Fact]
    public async Task ConfirmExit_NothingUnsaved_ExitsWithoutQuestions()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");

        (await viewModel.ConfirmExitAsync()).ShouldBeTrue();

        viewModel.ExitConfirmed.ShouldBeTrue();
        viewModel.HasUnsavedDocuments.ShouldBeFalse();
        await _messages.DidNotReceiveWithAnyArgs().AskAsync(default!, default!, default!, default, default!);
    }

    [Fact]
    public async Task ConfirmExit_CancelOnTheSecondDocument_DiscardsNothing()
    {
        var (viewModel, a, b) = await CreateTwoDirtyAsync();
        AskSaveChanges("a.pdf").Returns(DialogChoice.Secondary);
        AskSaveChanges("b.pdf").Returns(DialogChoice.Cancel);

        (await viewModel.ConfirmExitAsync()).ShouldBeFalse();

        viewModel.ExitConfirmed.ShouldBeFalse();
        a.DiscardChanges.ShouldBeFalse("nothing is discarded unless the exit goes ahead");
        b.DiscardChanges.ShouldBeFalse();
        viewModel.Documents.Count().ShouldBe(2);
    }

    [Fact]
    public async Task ConfirmExit_EveryDocumentAnswered_SavesOrDiscardsThenConfirms()
    {
        var (viewModel, a, b) = await CreateTwoDirtyAsync();
        SaverMarksSaved();
        AskSaveChanges("a.pdf").Returns(DialogChoice.Secondary);
        AskSaveChanges("b.pdf").Returns(DialogChoice.Primary);

        (await viewModel.ConfirmExitAsync()).ShouldBeTrue();

        viewModel.ExitConfirmed.ShouldBeTrue();
        a.DiscardChanges.ShouldBeTrue();
        b.IsDirty.ShouldBeFalse();
        viewModel.HasUnsavedDocuments.ShouldBeTrue("a.pdf still has its (discarded) changes until it closes");

        viewModel.CancelExit();

        viewModel.ExitConfirmed.ShouldBeFalse();
        a.DiscardChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task ConfirmExit_StopsTheRunningPrintJob()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        _printDialogs.ShowPrintDialog(3, 0).Returns(Job([0, 1, 2]));
        var printing = new TaskCompletionSource<PrintOutcome>();
        var token = CancellationToken.None;
        AnyPrint().Returns(call =>
        {
            token = call.ArgAt<CancellationToken>(4);
            return printing.Task;
        });
        var print = viewModel.PrintCommand.ExecuteAsync(null);

        var exit = viewModel.ConfirmExitAsync();

        token.IsCancellationRequested.ShouldBeTrue();
        exit.IsCompleted.ShouldBeFalse("the job must stop using the documents first");
        printing.SetResult(new PrintOutcome(Cancelled: true, FailedPages: 0));
        (await exit).ShouldBeTrue();
        await print;
    }

    [Fact]
    public async Task CloseAllDocuments_UnsavedDocumentNotDiscarded_KeepsItsSessionForRecovery()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        var sessionId = viewModel.ActiveDocument!.Workspace.Session.Id;
        viewModel.ExitConfirmed = true; // Windows is shutting down

        viewModel.CloseAllDocuments();

        var orphans = _fixture.Store.FindOrphans();
        try
        {
            var orphan = orphans.ShouldHaveSingleItem();
            orphan.Folder.Id.ShouldBe(sessionId);
            orphan.Manifest.ShouldNotBeNull().IsDirty.ShouldBeTrue();
        }
        finally
        {
            foreach (var orphan in orphans)
            {
                _fixture.Store.Delete(orphan.Folder);
            }
        }
    }

    [Fact]
    public async Task CloseAllDocuments_DiscardedDocument_RemovesItsSession()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        AskSaveChanges("a.pdf").Returns(DialogChoice.Secondary);
        (await viewModel.ConfirmExitAsync()).ShouldBeTrue();

        viewModel.CloseAllDocuments();

        _fixture.Store.FindOrphans().ShouldBeEmpty();
        Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecoverSessions_OpensTheRecoveredDocuments()
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(2), @"C:\docs\recovered.pdf");
        _recovery.RecoverAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DocumentWorkspace>>([workspace]));
        var viewModel = Create(isDebugBuild: false);

        await viewModel.RecoverSessionsAsync();

        viewModel.ActiveDocument.ShouldNotBeNull().Workspace.ShouldBeSameAs(workspace);
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Recovery_Done, 1));
    }

    [Fact]
    public async Task RecoverSessions_NothingToRecover_ChangesNothing()
    {
        _recovery.RecoverAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DocumentWorkspace>>([]));
        var viewModel = Create(isDebugBuild: false);

        await viewModel.RecoverSessionsAsync();

        viewModel.Documents.ShouldBeEmpty();
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Status_Ready);
    }

    [Fact]
    public async Task RecoverSessions_FolderProblem_IsLoggedNotThrown()
    {
        _recovery.RecoverAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
        var viewModel = Create(isDebugBuild: false);

        await viewModel.RecoverSessionsAsync();

        viewModel.Documents.ShouldBeEmpty();
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);

    /// <summary>Any call to the saver, for setting up its result.</summary>
    private Task AnySave()
        => _saver.SaveAsync(Arg.Any<DocumentWorkspace>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());

    private Task DidNotSave() => _saver.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default!, TestToken);

    /// <summary>The saver succeeds and marks the document saved, as the real one does.</summary>
    private void SaverMarksSaved()
        => AnySave().Returns(call =>
        {
            call.ArgAt<DocumentWorkspace>(0).CompleteSave(call.ArgAt<string>(1), null);
            return Task.CompletedTask;
        });

    private Task<DialogChoice> AskSigned()
        => _messages.AskAsync(Strings.Save_Signed_Title, Arg.Any<string>(), Strings.Save_SaveAsButton, Strings.Save_OverwriteAnyway, Strings.Common_Cancel);

    private Task<DialogChoice> AskSaveChanges(string fileName)
        => _messages.AskAsync(
            Strings.Close_SaveChanges_Title,
            Format(Strings.Close_SaveChanges_Message, fileName),
            Strings.Close_Save,
            Strings.Close_DontSave,
            Strings.Common_Cancel);

    /// <summary>A document opened from a real file (so its folder exists and its stamp is known).</summary>
    private async Task<(MainWindowViewModel ViewModel, string Path)> CreateWithFileAsync(string fileName, FakePdfDocument? document = null)
    {
        var path = _fixture.CreateFile(fileName);
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _fixture.OpenAsync(call.ArgAt<string>(0) == path ? document ?? FakePdfDocument.WithPages(3) : FakePdfDocument.WithPages(3), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        return (viewModel, path);
    }

    /// <summary>An open document with one unsaved change.</summary>
    private async Task<(MainWindowViewModel ViewModel, string Path)> CreateDirtyAsync(FakePdfDocument? document = null)
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf", document);
        viewModel.RotateRightCommand.Execute(null);
        viewModel.ActiveDocument!.IsDirty.ShouldBeTrue();
        return (viewModel, path);
    }

    private async Task<(MainWindowViewModel ViewModel, DocumentTabViewModel A, DocumentTabViewModel B)> CreateTwoDirtyAsync()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        var a = viewModel.ActiveDocument!;
        await viewModel.OpenFilesAsync([_fixture.CreateFile("b.pdf")]);
        viewModel.RotateRightCommand.Execute(null);
        var b = viewModel.ActiveDocument!;
        b.IsDirty.ShouldBeTrue();
        return (viewModel, a, b);
    }
}

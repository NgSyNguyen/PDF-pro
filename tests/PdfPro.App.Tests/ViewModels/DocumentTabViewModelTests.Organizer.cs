using System.Globalization;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The page organizer mode and the insert / extract commands of a document tab.</summary>
public sealed partial class DocumentTabViewModelTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrganizeMode_OpensTheGridOnTheCurrentPage()
    {
        await using var tab = await CreateAsync(5);
        tab.Viewer.NavigateTo(2);

        tab.ToggleOrganizeModeCommand.Execute(null);

        tab.IsOrganizing.ShouldBeTrue();
        var organizer = tab.Organizer.ShouldNotBeNull();
        organizer.Tiles.Count.ShouldBe(5);
        organizer.SelectedIndices.ShouldBe([2]);
        tab.TargetPageIndices.ShouldBe([2]);
    }

    [Fact]
    public async Task InTheOrganizer_PageCommandsActOnTheSelection()
    {
        await using var tab = await OrganizingAsync(6, [0, 3]);

        tab.RotateRightCommand.Execute(null);

        tab.Workspace.Session.Plan.Select(p => p.RotationDelta).ShouldBe([90, 0, 0, 90, 0, 0]);
        tab.Organizer!.SelectedIndices.ShouldBe([0, 3]);

        tab.DuplicatePagesCommand.Execute(null);

        tab.Viewer.PageCount.ShouldBe(8);
        tab.Organizer.SelectedIndices.ShouldBe([4, 5]); // the copies follow the last selected page

        tab.DeletePagesCommand.Execute(null);

        tab.Viewer.PageCount.ShouldBe(6);
        tab.Organizer.SelectedIndices.ShouldBe([4]);
    }

    [Fact]
    public async Task InTheOrganizer_WithoutSelection_PageCommandsAreDisabled()
    {
        await using var tab = await OrganizingAsync(4, []);

        tab.HasTargetPages.ShouldBeFalse();
        tab.RotateRightCommand.CanExecute(null).ShouldBeFalse();
        tab.DeletePagesCommand.CanExecute(null).ShouldBeFalse();
        tab.ExtractPagesCommand.CanExecute(null).ShouldBeFalse();
        tab.InsertBlankPagesCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task InTheOrganizer_EveryPageSelected_CannotBeDeleted()
    {
        await using var tab = await OrganizingAsync(3, [0, 1, 2]);

        tab.DeletePagesCommand.CanExecute(null).ShouldBeFalse();
        tab.RotateLeftCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task SelectionChanges_ReevaluateThePageCommands()
    {
        await using var tab = await OrganizingAsync(3, []);
        var raised = 0;
        tab.RotateRightCommand.CanExecuteChanged += (_, _) => raised++;

        tab.Organizer!.Select([1]);

        raised.ShouldBeGreaterThan(0);
        tab.RotateRightCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task LeavingTheOrganizer_ShowsTheSelectedPage()
    {
        await using var tab = await OrganizingAsync(6, [4]);

        tab.ToggleOrganizeModeCommand.Execute(null);

        tab.IsOrganizing.ShouldBeFalse();
        tab.Viewer.CurrentPageIndex.ShouldBe(4);
        tab.TargetPageIndices.ShouldBe([4]);
    }

    [Fact]
    public async Task OpenPage_LeavesTheOrganizerAtThatPage()
    {
        await using var tab = await OrganizingAsync(6, [1]);

        tab.Organizer!.OpenPageCommand.Execute(tab.Organizer.Tiles[5]);

        tab.IsOrganizing.ShouldBeFalse();
        tab.Viewer.CurrentPageIndex.ShouldBe(5);
    }

    [Fact]
    public async Task MovePages_IsOneUndoableStep()
    {
        await using var tab = await OrganizingAsync(5, [0, 1]);
        var keys = tab.Workspace.Session.Plan.Select(p => p.PageKey).ToArray();

        tab.MovePages([0, 1], 5).ShouldBeTrue();

        tab.Workspace.Session.Plan.Select(p => p.PageKey).ShouldBe([keys[2], keys[3], keys[4], keys[0], keys[1]]);
        tab.Organizer!.SelectedIndices.ShouldBe([3, 4]);
        tab.UndoToolTip.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Cmd_UndoWith, string.Format(CultureInfo.CurrentCulture, Strings.History_Move, 2)));

        tab.UndoCommand.Execute(null);

        tab.Workspace.Session.Plan.Select(p => p.PageKey).ShouldBe(keys);
    }

    [Fact]
    public async Task MovePages_ToWhereTheyAre_ChangesNothing()
    {
        await using var tab = await OrganizingAsync(5, [1]);

        tab.MovePages([1], 1).ShouldBeFalse();

        tab.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task InsertBlankPages_InsertsWhatTheDialogSays()
    {
        await using var tab = await OrganizingAsync(3, [1]);
        _pageDialogs.ShowInsertBlankPages(Arg.Any<InsertBlankPagesViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<InsertBlankPagesViewModel>();
            dialog.Position.PageNumberText.ShouldBe("2"); // after the selected page
            dialog.CountText = "2";
            dialog.InsertCommand.Execute(null);
            return true;
        });

        tab.InsertBlankPagesCommand.Execute(null);

        var plan = tab.Workspace.Session.Plan;
        plan.Count.ShouldBe(5);
        plan[2].BlankSize.ShouldBe(new PdfSize(595, 842));
        plan[3].BlankSize.ShouldBe(new PdfSize(595, 842));
        tab.Organizer!.SelectedIndices.ShouldBe([2, 3]);
    }

    [Fact]
    public async Task InsertBlankPages_Cancelled_ChangesNothing()
    {
        await using var tab = await CreateAsync(3);
        _pageDialogs.ShowInsertBlankPages(Arg.Any<InsertBlankPagesViewModel>()).Returns(false);

        tab.InsertBlankPagesCommand.Execute(null);

        tab.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task InsertPagesFromFile_CopiesTheFileIntoTheSessionAndInsertsTheChosenPages()
    {
        await using var tab = await CreateAsync(3);
        var inserted = FakePdfDocument.WithPages(4, new PdfSize(612, 792));
        var path = _fixture.CreateFile("b.pdf");
        _fileDialogs.PickPdfFile(Strings.Insert_PickFile_Title).Returns(path);
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(inserted);
        _pageDialogs.ShowInsertPages(Arg.Any<InsertPagesViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<InsertPagesViewModel>();
            dialog.SourcePageCount.ShouldBe(4);
            dialog.FileName.ShouldBe("b.pdf");
            dialog.IsSomePages = true;
            dialog.SourcePagesText = "2-3";
            dialog.Position.IsBefore = true;
            dialog.Position.IsFirstPage = true;
            dialog.InsertCommand.Execute(null);
            return true;
        });

        await tab.InsertPagesFromFileCommand.ExecuteAsync(null);

        var plan = tab.Workspace.Session.Plan;
        plan.Select(p => (p.SourceId, p.SourcePageIndex)).ShouldBe([("s1", 1), ("s1", 2), ("s0", 0), ("s0", 1), ("s0", 2)]);
        tab.Viewer.Document.PageSizes[0].ShouldBe(new PdfSize(612, 792));
        tab.Workspace.Session.Sources["s1"].DisplayName.ShouldBe("b.pdf");
        File.Exists(Path.Combine(tab.Workspace.Session.Directory, "sources", "s1.pdf")).ShouldBeTrue();
        tab.UndoToolTip.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Cmd_UndoWith, string.Format(CultureInfo.CurrentCulture, Strings.History_InsertPages, 2, "b.pdf")));
    }

    [Fact]
    public async Task InsertPagesFromFile_PickingNothing_ChangesNothing()
    {
        await using var tab = await CreateAsync(3);
        _fileDialogs.PickPdfFile(Arg.Any<string>()).Returns((string?)null);

        await tab.InsertPagesFromFileCommand.ExecuteAsync(null);

        tab.Workspace.Sources.Count.ShouldBe(1);
        _pageDialogs.DidNotReceiveWithAnyArgs().ShowInsertPages(default!);
    }

    [Fact]
    public async Task InsertPagesFromFile_PasswordProtectedFile_IsExplained()
    {
        await using var tab = await CreateAsync(3);
        _fileDialogs.PickPdfFile(Arg.Any<string>()).Returns(_fixture.CreateFile("secret.pdf"));
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new PdfPasswordRequiredException());

        await tab.InsertPagesFromFileCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Insert_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.Insert_Error_Encrypted, "secret.pdf"));
        tab.Workspace.Sources.Count.ShouldBe(1);
        File.Exists(Path.Combine(tab.Workspace.Session.Directory, "sources", "s1.pdf")).ShouldBeFalse();
        _pageDialogs.DidNotReceiveWithAnyArgs().ShowInsertPages(default!);
    }

    [Fact]
    public async Task InsertPagesFromFile_EncryptedFileWithoutOpenPassword_IsRefusedToo()
    {
        await using var tab = await CreateAsync(3);
        var encrypted = FakePdfDocument.WithPages(2);
        encrypted.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 3 };
        _fileDialogs.PickPdfFile(Arg.Any<string>()).Returns(_fixture.CreateFile("owner-only.pdf"));
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(encrypted);

        await tab.InsertPagesFromFileCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Insert_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.Insert_Error_Encrypted, "owner-only.pdf"));
        encrypted.IsDisposed.ShouldBeTrue();
        tab.Workspace.Sources.Count.ShouldBe(1);
    }

    [Fact]
    public async Task InsertPagesFromFile_MissingFile_IsExplained()
    {
        await using var tab = await CreateAsync(3);
        _fileDialogs.PickPdfFile(Arg.Any<string>()).Returns(Path.Combine(Path.GetDirectoryName(_fixture.CreateFile("here.pdf"))!, "gone.pdf"));

        await tab.InsertPagesFromFileCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Insert_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.Open_Error_FileNotFound, "gone.pdf"));
        tab.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task WhileAFileIsBeingAdded_PageCommandsWait()
    {
        await using var tab = await CreateAsync(3);
        var pending = new TaskCompletionSource<IPdfDocument>();
        _fileDialogs.PickPdfFile(Arg.Any<string>()).Returns(_fixture.CreateFile("slow.pdf"));
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        _pageDialogs.ShowInsertPages(Arg.Any<InsertPagesViewModel>()).Returns(false);

        var inserting = tab.InsertPagesFromFileCommand.ExecuteAsync(null);
        await Task.Delay(50, TestToken);

        tab.RotateRightCommand.CanExecute(null).ShouldBeFalse();
        tab.InsertBlankPagesCommand.CanExecute(null).ShouldBeFalse();
        pending.SetResult(FakePdfDocument.WithPages(1));
        await inserting;
        tab.RotateRightCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task DroppedFiles_AreInsertedInOrderAtTheDropPoint()
    {
        await using var tab = await OrganizingAsync(3, [0]);
        var first = _fixture.CreateFile("one.pdf");
        var second = _fixture.CreateFile("two.pdf");
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => FakePdfDocument.WithPages(2), _ => FakePdfDocument.WithPages(1));

        await tab.InsertFilesAsync([first, second], 1);

        tab.Workspace.Session.Plan.Select(p => p.SourceId).ShouldBe(["s0", "s1", "s1", "s2", "s0", "s0"]);
        tab.Workspace.Session.History.UndoCount.ShouldBe(2);
        tab.Organizer!.SelectedIndices.ShouldBe([3]); // the last insert's pages
    }

    [Fact]
    public async Task ExtractPages_AsksTheWindowToWriteTheTargetPages()
    {
        await using var tab = await OrganizingAsync(6, [1, 4]);
        IReadOnlyList<int>? requested = null;
        tab.ExtractRequested += (_, pages) => requested = pages;

        tab.ExtractPagesCommand.Execute(null);

        requested.ShouldBe([1, 4]);
        tab.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task EncryptedDocument_CannotBeOrganizedEdited()
    {
        var document = FakePdfDocument.WithPages(3);
        document.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 6 };
        await using var tab = _factory.Create(await _fixture.CreateAsync(document));
        tab.ToggleOrganizeModeCommand.Execute(null);

        tab.CanEditPages.ShouldBeFalse();
        tab.InsertBlankPagesCommand.CanExecute(null).ShouldBeFalse();
        tab.ExtractPagesCommand.CanExecute(null).ShouldBeFalse();
        tab.MovePages([0], 3).ShouldBeFalse();
    }

    [Fact]
    public async Task CropPages_AppliesTheDialogsMarginsAsOneStep()
    {
        await using var tab = await OrganizingAsync(4, [1, 2]);
        _pageDialogs.ShowCropPages(Arg.Any<CropPagesViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<CropPagesViewModel>();
            dialog.Target.IsSelected.ShouldBeTrue();
            dialog.PreviewPage.ShouldBeSameAs(tab.Workspace.Pages.Pages[1]);
            dialog.LeftText = "10";
            dialog.ApplyCommand.Execute(null);
            return true;
        });

        tab.CropPagesCommand.Execute(null);

        var plan = tab.Workspace.Session.Plan;
        plan.Select(p => p.CropBoxOverride is not null).ShouldBe([false, true, true, false]);
        tab.Viewer.Document.PageSizes[1].Width.ShouldBe(595 - (10 * 72 / 25.4), 0.01);
        tab.UndoToolTip.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Cmd_UndoWith, string.Format(CultureInfo.CurrentCulture, Strings.History_Crop, 2)));
    }

    [Fact]
    public async Task CropPages_Cancelled_ChangesNothing()
    {
        await using var tab = await CreateAsync(2);
        _pageDialogs.ShowCropPages(Arg.Any<CropPagesViewModel>()).Returns(false);

        tab.CropPagesCommand.Execute(null);

        tab.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangePageSize_AsksTheWindowToRunIt()
    {
        await using var tab = await OrganizingAsync(5, [0, 4]);
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        _pageDialogs.ShowPageSize(Arg.Any<PageSizeViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<PageSizeViewModel>();
            dialog.SelectedFormat = dialog.Formats.Single(f => f.Format == "A5");
            dialog.ApplyCommand.Execute(null);
            return true;
        });

        tab.ChangePageSizeCommand.Execute(null);

        request.ShouldNotBeNull();
        request.Operation.ShouldBeOfType<ChangePageSizeOperation>();
        request.Parameters.ShouldBe(new ChangePageSizeParams("1, 5", 420, 595));
        request.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_PageSize, 2));
        tab.IsDirty.ShouldBeFalse("the window applies it");
    }

    [Fact]
    public async Task ChangePageSize_Cancelled_AsksNothing()
    {
        await using var tab = await CreateAsync(2);
        var requested = false;
        tab.ContentChangeRequested += (_, _) => requested = true;
        _pageDialogs.ShowPageSize(Arg.Any<PageSizeViewModel>()).Returns(false);

        tab.ChangePageSizeCommand.Execute(null);

        requested.ShouldBeFalse();
    }

    private async Task<DocumentTabViewModel> OrganizingAsync(int pages, int[] selection)
    {
        var tab = await CreateAsync(pages);
        tab.ToggleOrganizeModeCommand.Execute(null);
        tab.Organizer!.Select(selection);
        return tab;
    }
}

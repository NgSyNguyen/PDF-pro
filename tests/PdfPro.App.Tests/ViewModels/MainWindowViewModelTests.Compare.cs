using NSubstitute;
using PdfPro.App.Services;
using PdfPro.App.Services.Comparing;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Comparing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"So sánh" in the ribbon: the window opens with the active document as the old version and another open one as the new version.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task Compare_NeedsNoOpenDocument()
    {
        var viewModel = Create(isDebugBuild: false);
        CompareViewModel? shown = null;
        _pageDialogs.ShowCompare(Arg.Do<CompareViewModel>(d => shown = d)).Returns(false);

        await viewModel.CompareDocumentsCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull();
        (shown.OldPath, shown.NewPath).ShouldBe((null, null));
    }

    [Fact]
    public async Task Compare_StartsWithTheActiveDocument_AgainstAnotherOpenOne()
    {
        var first = _fixture.CreateFile("hợp đồng 1.pdf", File.ReadAllBytes(Samples.ContractV1));
        var second = _fixture.CreateFile("hợp đồng 2.pdf", File.ReadAllBytes(Samples.ContractV2));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(4), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([first]);
        await viewModel.OpenFilesAsync([second]);
        CompareViewModel? shown = null;
        _pageDialogs.ShowCompare(Arg.Do<CompareViewModel>(d => shown = d)).Returns(false);

        await viewModel.CompareDocumentsCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull();
        (shown.OldName, shown.NewName).ShouldBe(("hợp đồng 2.pdf", "hợp đồng 1.pdf"));
        (shown.OldPath, shown.NewPath).ShouldBe((viewModel.ActiveDocument!.FilePath, viewModel.Documents.First().FilePath));
    }

    [Fact]
    public async Task ExtractTables_OpensTheTableWindow_OnTheActiveDocument()
    {
        var path = _fixture.CreateFile("bảng biểu.pdf", File.ReadAllBytes(Samples.Tables));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(2), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        PdfPro.App.ViewModels.Extracting.TablesViewModel? shown = null;
        _pageDialogs.ShowTables(Arg.Do<PdfPro.App.ViewModels.Extracting.TablesViewModel>(d => shown = d)).Returns(false);

        await viewModel.ExtractTablesCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull();
        shown.FileName.ShouldBe("bảng biểu.pdf");
        shown.PageCount.ShouldBe(2);
    }

    [Fact]
    public async Task ExtractImages_WritesTheScansIntoTheFolderNextToTheDocument()
    {
        var path = _fixture.CreateFile("bản quét.pdf", File.ReadAllBytes(Samples.ScanVi));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(18), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        PdfPro.App.ViewModels.Extracting.ExtractImagesViewModel? shown = null;
        _pageDialogs.ShowExtractImages(Arg.Do<PdfPro.App.ViewModels.Extracting.ExtractImagesViewModel>(d =>
        {
            shown = d;
            d.OpenFolderWhenDone = false;
            d.ApplyCommand.Execute(null);
        })).Returns(true);

        await viewModel.ExtractImagesCommand.ExecuteAsync(null);

        var folder = Path.Combine(Path.GetDirectoryName(path)!, string.Format(System.Globalization.CultureInfo.CurrentCulture, PdfPro.App.Resources.Strings.ExtractImages_Folder_Default, "bản quét"));
        shown.ShouldNotBeNull().OutputFolder.ShouldBe(folder);
        Directory.GetFiles(folder, "*.jpg").Length.ShouldBeGreaterThanOrEqualTo(14);
        await _messages.Received(1).ShowInfoAsync(PdfPro.App.Resources.Strings.ExtractImages_Title, Arg.Any<string>());
        _shell.DidNotReceiveWithAnyArgs().OpenFolder(default!);
    }

    [Fact]
    public async Task Attachments_ChangedInTheDialog_BecomeOneContentChange()
    {
        var path = _fixture.CreateFile("văn bản.pdf", File.ReadAllBytes(Samples.Basic));
        var note = _fixture.CreateFile("ghi chú.txt", "x"u8.ToArray());
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        _fileDialogs.PickAttachmentFiles(Arg.Any<string>()).Returns([note]);
        _pageDialogs.ShowAttachments(Arg.Do<PdfPro.App.ViewModels.Extracting.AttachmentsViewModel>(d =>
        {
            d.AddCommand.Execute(null);
            d.ApplyCommand.Execute(null);
        })).Returns(true);

        await viewModel.AttachmentsCommand.ExecuteAsync(null);

        // The tab asks the window for the change through an event; give it the moment it may take.
        for (var i = 0; i < 250 && !_contentChanger.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(PdfPro.App.Services.Documents.IContentChanger.ApplyAsync)); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var call = _contentChanger.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(PdfPro.App.Services.Documents.IContentChanger.ApplyAsync));
        call.GetArguments()[1].ShouldBeOfType<PdfPro.Operations.Extracting.EditAttachmentsOperation>();
        call.GetArguments()[2].ShouldBeOfType<PdfPro.Operations.Extracting.EditAttachmentsParams>().Add.ShouldNotBeNull().ShouldHaveSingleItem().Path.ShouldBe(note);
    }

    [Fact]
    public async Task Bookmarks_EditedInTheDialog_BecomeOneContentChange()
    {
        var path = _fixture.CreateFile("văn bản.pdf", File.ReadAllBytes(Samples.Basic));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        _pageDialogs.ShowBookmarks(Arg.Do<PdfPro.App.ViewModels.Structure.BookmarksEditorViewModel>(d =>
        {
            d.AddCommand.Execute(null);
            d.ApplyCommand.Execute(null);
        })).Returns(true);

        await viewModel.BookmarksCommand.ExecuteAsync(null);

        for (var i = 0; i < 250 && !_contentChanger.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(PdfPro.App.Services.Documents.IContentChanger.ApplyAsync)); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var call = _contentChanger.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(PdfPro.App.Services.Documents.IContentChanger.ApplyAsync));
        call.GetArguments()[1].ShouldBeOfType<PdfPro.Operations.Structure.SetBookmarksOperation>();
        // basic-vi.pdf has four bookmarks of its own; the new one comes after them.
        var bookmarks = call.GetArguments()[2].ShouldBeOfType<PdfPro.Operations.Structure.SetBookmarksParams>().Bookmarks;
        bookmarks.Select(b => b.Title).ShouldBe(["Trang bìa", "Chương I. Quy định chung", "Chương II. Tổ chức thực hiện", "Phụ lục", PdfPro.App.Resources.Strings.Bookmarks_NewTitle]);
        bookmarks[1].Children.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AddBookmark_WithoutASelection_ProposesThePageShown_AmongTheOthersInPageOrder()
    {
        var path = _fixture.CreateFile("văn bản.pdf", File.ReadAllBytes(Samples.Basic));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        var title = Format(PdfPro.App.Resources.Strings.Bookmarks_PageOf, 1);
        string? chosen = null;
        _pageDialogs.ShowBookmarks(Arg.Do<PdfPro.App.ViewModels.Structure.BookmarksEditorViewModel>(d =>
        {
            chosen = d.Selected?.Title;
            d.ApplyCommand.Execute(null);
        })).Returns(true);

        await viewModel.AddBookmarkCommand.ExecuteAsync(null);

        chosen.ShouldBe(title, "the new bookmark is chosen, its title ready to be typed over");
        for (var i = 0; i < 250 && !_contentChanger.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(PdfPro.App.Services.Documents.IContentChanger.ApplyAsync)); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var call = _contentChanger.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(PdfPro.App.Services.Documents.IContentChanger.ApplyAsync));
        var bookmarks = call.GetArguments()[2].ShouldBeOfType<PdfPro.Operations.Structure.SetBookmarksParams>().Bookmarks;
        bookmarks.Count.ShouldBe(5, "basic-vi.pdf's four bookmarks and the new one");
        var index = bookmarks.Select(b => b.Title).ToList().IndexOf(title);
        bookmarks[index].Page.ShouldBe(0);
        bookmarks[index].Top.ShouldBeNull();
        bookmarks.Take(index).ShouldAllBe(b => b.Page <= 0);
        bookmarks.Skip(index + 1).ShouldAllBe(b => b.Page > 0);
    }

    [Theory]
    [InlineData("  Điều 3.\tHiệu   lực\r\nnội dung", "Điều 3. Hiệu lực")]
    [InlineData("\r\n\r\nPhụ lục", "Phụ lục")]
    [InlineData("", "")]
    public void ABookmarkFromSelectedText_IsTitledWithItsFirstLine(string text, string title)
        => PdfPro.App.ViewModels.MainWindowViewModel.FirstLine(text).ShouldBe(title);

    [Fact]
    public void ABookmarkTitle_IsCutAtOneHundredSixtyCharacters()
        => PdfPro.App.ViewModels.MainWindowViewModel.FirstLine(new string('a', 200)).ShouldBe(new string('a', 160) + "…");

    private PdfPro.App.Services.Extracting.ExtractingServices Extracting()
        => new(new PdfPro.Operations.Extracting.ExtractTablesOperation(), Substitute.For<IPdfDocumentLoader>(), new FakePageImageRenderer(), new PdfPro.Operations.Extracting.ExtractImagesOperation(TestOperations.PdfiumGate));

    private CompareServices Compare()
        => new(Substitute.For<IDocumentComparison>(), Substitute.For<IPdfDocumentLoader>(), new FakePageImageRenderer(), Substitute.For<IPasswordPrompt>());
}

using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Operations.Converting;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"PDF sang văn bản": format, page split, pages, and the names written.</summary>
public sealed class ToTextViewModelTests
{
    private const string Folder = @"C:\docs";

    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();

    [Fact]
    public void ItStarts_WithPlainTextOfEveryPage_PagesBroken_NextToTheDocument()
    {
        var dialog = new ToTextViewModel("hồ sơ.pdf", 6, [], Folder, _files);

        dialog.IsText.ShouldBeTrue();
        dialog.IsPageBreaks.ShouldBeTrue();
        dialog.NamesText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.ToText_Names, "hồ sơ.txt"));
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldBe(new ToTextDialogResult(new ToTextParams(), Folder, OpenFolder: false));
    }

    [Fact]
    public void MarkdownOfOddPages_OneFilePerPage_IsWhatIsChosen()
    {
        var dialog = new ToTextViewModel("hồ sơ.pdf", 6, [], Folder, _files);
        dialog.IsMarkdown = true;
        dialog.IsFilePerPage = true;
        dialog.Target.IsOdd = true;
        dialog.Overwrite = true;

        dialog.ApplyCommand.Execute(null);

        dialog.NamesText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.ToText_Names, "hồ sơ-001.md, hồ sơ-002.md…"));
        dialog.Result.ShouldNotBeNull().Parameters.ShouldBe(new ToTextParams(TextFileFormat.Markdown, TextPageSplit.FilePerPage, "odd", Overwrite: true));
    }

    [Fact]
    public void AFolderWithoutAFullPath_IsRefused_AndAPickedOneIsUsed()
    {
        var dialog = new ToTextViewModel("hồ sơ.pdf", 6, [1, 2], Folder, _files);
        dialog.Target.IsSelected.ShouldBeTrue("the pages selected in the organizer are offered");

        dialog.OutputFolder = "văn bản";

        dialog.ErrorText.ShouldBe(Strings.ToImages_Error_Folder);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
        _files.PickFolder(Strings.ToText_Folder_Pick_Title, "văn bản").Returns(@"D:\Văn bản");
        dialog.PickFolderCommand.Execute(null);
        dialog.ErrorText.ShouldBeNull();
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldNotBeNull().Parameters.Pages.ShouldBe("2-3");
    }
}

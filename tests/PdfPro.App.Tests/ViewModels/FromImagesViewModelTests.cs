using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Operations.Converting;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Ảnh sang PDF": the pictures in natural order, the page size, the PDF to write.</summary>
public sealed class FromImagesViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void PicturesAdded_AreInNaturalOrder_OtherFilesLeftOut_AndTheFirstNamesThePdf()
    {
        var paths = Files("trang 10.jpg", "trang 2.png", "ghi chú.txt", "trang 1.TIF");
        _files.PickImageFiles(Strings.FromImages_Pick_Title).Returns(paths);
        var dialog = new FromImagesViewModel(_files);

        dialog.AddFilesCommand.Execute(null);

        dialog.Items.Select(i => i.FileName).ShouldBe(["trang 1.TIF", "trang 2.png", "trang 10.jpg"]);
        dialog.SummaryText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.FromImages_Summary, 3));
        dialog.OutputPath.ShouldBe(_temp.Combine("trang 1.pdf"));
        dialog.ErrorText.ShouldBeNull();
    }

    [Fact]
    public void AFolder_AddsItsPictures()
    {
        var folder = _temp.Combine("scan");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "p3.jpg", "p1.jpg", "đọc tôi.txt" })
        {
            File.WriteAllBytes(Path.Combine(folder, name), [1]);
        }

        _files.PickFolder(Strings.FromImages_Folder_Pick_Title, null).Returns(folder);
        var dialog = new FromImagesViewModel(_files);

        dialog.AddFolderCommand.Execute(null);

        dialog.Items.Select(i => i.FileName).ShouldBe(["p1.jpg", "p3.jpg"]);
    }

    [Fact]
    public void ThePicturesCanBeReordered_AndSortedAgain()
    {
        var dialog = new FromImagesViewModel(_files, Files("a1.jpg", "a2.jpg", "a3.jpg"));

        dialog.MoveDownCommand.Execute(dialog.Items[0]);
        dialog.MoveUpCommand.Execute(dialog.Items[2]);
        dialog.Items.Select(i => i.FileName).ShouldBe(["a2.jpg", "a3.jpg", "a1.jpg"]);
        dialog.RemoveCommand.Execute(dialog.Items[1]);
        dialog.SortByNameCommand.Execute(null);

        dialog.Items.Select(i => i.FileName).ShouldBe(["a1.jpg", "a2.jpg"]);
    }

    [Fact]
    public void Run_GivesThePicturesInOrder_ThePageSize_AndThePdf()
    {
        var dialog = new FromImagesViewModel(_files, Files("b.jpg", "a.png"));
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;
        dialog.IsA4Margins = true;
        dialog.MarginText = "15";
        dialog.OpenWhenDone = false;

        dialog.RunCommand.Execute(null);

        closed.ShouldBe(true);
        dialog.Result.ShouldNotBeNull().ShouldSatisfyAllConditions(
            r => r.Files.ShouldBe([_temp.Combine("a.png"), _temp.Combine("b.jpg")]),
            r => r.Parameters.ShouldBe(new FromImagesParams(ImagePageSize.A4Margins, 15)),
            r => r.OutputPath.ShouldBe(_temp.Combine("a.pdf")),
            r => r.OpenWhenDone.ShouldBeFalse());
    }

    [Fact]
    public void WhatCannotBeUsed_IsExplained_AndBlocksMakingThePdf()
    {
        var dialog = new FromImagesViewModel(_files);
        dialog.ErrorText.ShouldBe(Strings.FromImages_Error_None);
        dialog.RunCommand.CanExecute(null).ShouldBeFalse();

        dialog.AddPaths(Files("x.jpg"));
        dialog.IsA4Margins = true;
        dialog.MarginText = "60";
        dialog.ErrorText.ShouldBe(Strings.FromImages_Error_Margin);

        dialog.MarginText = "5";
        dialog.OutputPath = "x.docx";
        dialog.ErrorText.ShouldBe(Strings.FromImages_Error_Output);

        dialog.OutputPath = _temp.Combine("x.pdf");
        dialog.ErrorText.ShouldBeNull();
        dialog.RunCommand.CanExecute(null).ShouldBeTrue();
    }

    private string[] Files(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllBytes(_temp.Combine(name), [1, 2, 3]);
        }

        return [.. names.Select(n => _temp.Combine(n))];
    }
}

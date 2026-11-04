using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Operations.Converting;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"PDF sang ảnh": format and its options, resolution with the size it gives, pages, folder.</summary>
public sealed class ToImagesViewModelTests
{
    private const string Folder = @"C:\docs\hồ sơ (ảnh)";

    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();

    [Fact]
    public void ItStarts_WithPngsOfEveryPage_At150Dpi_InTheProposedFolder()
    {
        var dialog = Create();

        dialog.IsPng.ShouldBeTrue();
        dialog.ErrorText.ShouldBeNull();
        dialog.SizeText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.ToImages_Size, 1240, 1754), "A4 at 150 DPI");
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldBe(new ToImagesDialogResult(new ToImagesParams(PageImageFormat.Png, 150), Folder, OpenFolder: true));
    }

    [Fact]
    public void TheSizeFollowsTheResolution()
    {
        var dialog = Create();

        dialog.DpiText = "300";

        dialog.SizeText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.ToImages_Size, 2480, 3508));
    }

    [Fact]
    public void ATiffOfOddPages_InBlackAndWhite_IsWhatIsChosen()
    {
        var dialog = Create();
        dialog.IsTiff = true;
        dialog.TiffColor = TiffColor.BlackWhite;
        dialog.OneTiff = false;
        dialog.Target.IsOdd = true;
        dialog.Transparent = true;

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldNotBeNull().Parameters.ShouldBe(new ToImagesParams(PageImageFormat.Tiff, 150, "odd", Transparent: false, TiffColor: TiffColor.BlackWhite, OneTiff: false), "transparency is for PNG only");
    }

    [Fact]
    public void TheSelectedPages_AreOffered_AndUsed()
    {
        var dialog = new ToImagesViewModel("hồ sơ.pdf", 10, [2, 3, 4], new PdfSize(595, 842), Folder, _files);

        dialog.Target.IsSelected.ShouldBeTrue();
        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldNotBeNull().Parameters.Pages.ShouldBe("3-5");
    }

    [Theory]
    [InlineData("35", null, "ToImages_Error_Dpi")]
    [InlineData("1201", null, "ToImages_Error_Dpi")]
    [InlineData("150", "9", "ToImages_Error_Quality")]
    [InlineData("150", "abc", "ToImages_Error_Quality")]
    public void ValuesOutOfRange_AreExplained_AndBlockWriting(string dpi, string? quality, string message)
    {
        var dialog = Create();
        dialog.IsJpeg = true;

        dialog.DpiText = dpi;
        if (quality is not null)
        {
            dialog.QualityText = quality;
        }

        dialog.ErrorText.ShouldBe(Strings.ResourceManager.GetString(message, CultureInfo.CurrentUICulture));
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void AFolderWithoutAFullPath_IsRefused_AndAPickedOneIsUsed()
    {
        var dialog = Create();

        dialog.OutputFolder = "ảnh";

        dialog.ErrorText.ShouldBe(Strings.ToImages_Error_Folder);
        _files.PickFolder(Strings.ToImages_Folder_Pick_Title, "ảnh").Returns(@"D:\Ảnh");
        dialog.PickFolderCommand.Execute(null);
        dialog.OutputFolder.ShouldBe(@"D:\Ảnh");
        dialog.ErrorText.ShouldBeNull();
    }

    private ToImagesViewModel Create() => new("hồ sơ.pdf", 10, [], new PdfSize(595.28, 841.89), Folder, _files);
}

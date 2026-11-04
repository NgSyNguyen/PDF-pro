using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Optimizing;
using PdfPro.Operations.Optimizing;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Thang xám": converting colours by default, pictures of the pages on request, a resolution in range.</summary>
public sealed class GrayscaleViewModelTests
{
    [Fact]
    public void ItStarts_ConvertingTheColours_At300Dpi()
    {
        var dialog = new GrayscaleViewModel("hồ sơ.pdf", 12);

        dialog.IsConvert.ShouldBeTrue();
        dialog.IsRaster.ShouldBeFalse();
        dialog.PagesText.ShouldBe(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Grayscale_Pages, 12));
        dialog.Parameters(out var error).ShouldBe(new GrayscaleParams(GrayscaleMode.Convert, 300));
        error.ShouldBeNull();
    }

    [Fact]
    public void PicturesOfThePages_AreChosen_WithTheirResolution()
    {
        var dialog = new GrayscaleViewModel("hồ sơ.pdf", 12);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.IsRaster = true;
        dialog.DpiText = "150";
        dialog.ApplyCommand.Execute(null);

        dialog.Mode.ShouldBe(GrayscaleMode.Raster);
        dialog.IsConvert.ShouldBeFalse();
        dialog.Result.ShouldBe(new GrayscaleParams(GrayscaleMode.Raster, 150));
        closed.ShouldBe(true);
    }

    [Theory]
    [InlineData("71")]
    [InlineData("601")]
    [InlineData("200,5")]
    [InlineData("")]
    public void AResolutionOutOfRange_IsExplained_AndBlocksConverting(string dpi)
    {
        var dialog = new GrayscaleViewModel("hồ sơ.pdf", 12);

        dialog.DpiText = dpi;

        dialog.ErrorText.ShouldBe(Strings.Grayscale_Error_Dpi);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Cancel_ClosesWithoutAResult()
    {
        var dialog = new GrayscaleViewModel("hồ sơ.pdf", 12);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.CancelCommand.Execute(null);

        closed.ShouldBe(false);
        dialog.Result.ShouldBeNull();
    }
}

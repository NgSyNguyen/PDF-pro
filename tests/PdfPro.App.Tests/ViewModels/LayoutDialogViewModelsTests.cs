using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The crop and page size dialogs and their "Áp dụng cho" part.</summary>
public sealed class LayoutDialogViewModelsTests
{
    private const double PointsPerMillimetre = 72 / 25.4;

    [Fact]
    public void Target_StartsOnTheSelection()
    {
        var target = new PageTargetViewModel(10, [2, 5]);

        target.IsSelected.ShouldBeTrue();
        target.SelectedLabel.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Target_Selected, 2));
        target.Indices.ShouldBe([2, 5]);
        target.Pages(out _).ShouldBe("3, 6");
        target.RangeText.ShouldBe("3, 6");
    }

    [Fact]
    public void Target_WithoutSelection_StartsOnEveryPage()
    {
        var target = new PageTargetViewModel(4, []);

        target.HasSelection.ShouldBeFalse();
        target.IsAll.ShouldBeTrue();
        target.Pages(out _).ShouldBe("1-4");
    }

    [Theory]
    [InlineData(PageTarget.All, "1-5", new[] { 0, 1, 2, 3, 4 })]
    [InlineData(PageTarget.Odd, "odd", new[] { 0, 2, 4 })]
    [InlineData(PageTarget.Even, "even", new[] { 1, 3 })]
    public void Target_Choices(PageTarget choice, string pages, int[] indices)
    {
        var target = new PageTargetViewModel(5, [1]) { Target = choice };

        target.Pages(out var error).ShouldBe(pages);
        error.ShouldBeNull();
        target.Indices.ShouldBe(indices);
    }

    [Fact]
    public void Target_Range_IsCheckedAndReported()
    {
        var target = new PageTargetViewModel(5, []);
        var changes = 0;
        target.Changed += (_, _) => changes++;

        target.IsRange = true;
        target.RangeText = "2-9";

        target.Pages(out var error).ShouldBeNull();
        error.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_OutOfRange, "2-9", 5));
        target.Indices.ShouldBeEmpty();
        changes.ShouldBe(2);

        target.RangeText = "4, 2";
        target.Pages(out _).ShouldBe("2, 4");
    }

    [Fact]
    public void Target_EvenPagesOfASinglePage_IsExplained()
    {
        var target = new PageTargetViewModel(1, []) { Target = PageTarget.Even };

        target.Pages(out var error).ShouldBeNull();
        error.ShouldBe(Strings.Target_Error_NoEvenPages);
    }

    [Fact]
    public void Crop_MillimetresBecomePoints()
    {
        var dialog = CropDialog(out _, [1]);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;
        dialog.LeftText = "10";
        dialog.TopText = "0";
        dialog.RightText = "5";
        dialog.BottomText = "2,5".Replace(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal);

        dialog.ApplyCommand.Execute(null);

        closed.ShouldBe(true);
        var result = dialog.Result.ShouldNotBeNull();
        result.Pages.ShouldBe("2");
        result.Left.ShouldBe(10 * PointsPerMillimetre, 1e-9);
        result.Right.ShouldBe(5 * PointsPerMillimetre, 1e-9);
        result.Bottom.ShouldBe(2.5 * PointsPerMillimetre, 1e-9);
        result.Reset.ShouldBeFalse();
    }

    [Fact]
    public void Crop_Uniform_TheOtherMarginsFollowTheLeftOne()
    {
        var dialog = CropDialog(out _, [0]);

        dialog.IsUniform = true;
        dialog.LeftText = "12";

        (dialog.TopText, dialog.RightText, dialog.BottomText).ShouldBe(("12", "12", "12"));
    }

    [Fact]
    public void Crop_Preview_ShadesWhatIsCut()
    {
        var dialog = CropDialog(out _, [0]);

        // A4 fitted into 300 × 380: 380 / 842 view units per point.
        var scale = CropPagesViewModel.PreviewBoxHeight / 842;
        dialog.PreviewHeight.ShouldBe(380, 1e-6);
        dialog.PreviewWidth.ShouldBe(595 * scale, 1e-6);
        dialog.KeptWidth.ShouldBe(dialog.PreviewWidth, 1e-6);

        dialog.LeftText = "10";
        dialog.BottomText = "20";

        dialog.KeptLeft.ShouldBe(10 * PointsPerMillimetre * scale, 1e-6);
        dialog.KeptTop.ShouldBe(0);
        dialog.KeptHeight.ShouldBe((842 - (20 * PointsPerMillimetre)) * scale, 1e-6);
        dialog.ShadeBottomHeight.ShouldBe(20 * PointsPerMillimetre * scale, 1e-6);
        dialog.ShadeRightWidth.ShouldBe(0, 1e-6);
        dialog.PreviewSize.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Insert_SizeMillimetres, 210, 297));
    }

    [Fact]
    public async Task Crop_Preview_LoadsTheFirstChosenPage()
    {
        var dialog = CropDialog(out var loads, [3, 1]);
        await Task.Yield();

        loads.ShouldHaveSingleItem().SourcePageIndex.ShouldBe(1);
        dialog.PreviewImage.ShouldNotBeNull();

        dialog.Target.IsOdd = true;

        loads.Count.ShouldBe(2);
        loads[1].SourcePageIndex.ShouldBe(0);
    }

    [Fact]
    public void Crop_Reset_IgnoresTheMargins()
    {
        var dialog = CropDialog(out _, [0]);
        dialog.LeftText = "not a number";
        dialog.IsReset = true;

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBe(new CropPagesParams("1", 0, 0, 0, 0, Reset: true));
        dialog.CanEditMargins.ShouldBeFalse();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("6000")]
    public void Crop_BadMargin_IsExplained(string margin)
    {
        var dialog = CropDialog(out _, [0]);
        dialog.TopText = margin;

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBeNull();
        dialog.Error.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Crop_Error_Margin, 5080));
    }

    [Fact]
    public void Crop_NothingToCut_IsExplained()
    {
        var dialog = CropDialog(out _, [0]);

        dialog.ApplyCommand.Execute(null);

        dialog.Error.ShouldBe(Strings.Crop_Error_Nothing);
    }

    [Fact]
    public void Crop_MarginsLargerThanAChosenPage_AreRefused()
    {
        var pages = Pages(new PdfSize(595, 842), new PdfSize(200, 200));
        var dialog = new CropPagesViewModel(pages, [], (_, _) => Task.FromResult<ImageSource?>(null)) { LeftText = "40", RightText = "40" };

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBeNull();
        dialog.Error.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Crop_Error_TooLarge, 2));
    }

    [Fact]
    public void PageSize_StandardFormatForTheSelection()
    {
        var dialog = new PageSizeViewModel(6, [0, 2], new PdfSize(595, 842));
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;
        dialog.SelectedFormat = dialog.Formats.Single(f => f.Format == "A5");

        dialog.ApplyCommand.Execute(null);

        closed.ShouldBe(true);
        dialog.Result.ShouldBe(new ChangePageSizeParams("1, 3", 420, 595, PageSizeFit.ScaleToFit, MatchOrientation: true));
    }

    [Fact]
    public void PageSize_Landscape_Centered()
    {
        var dialog = new PageSizeViewModel(6, [], new PdfSize(595, 842)) { IsLandscape = true, IsCentered = true };
        dialog.SelectedFormat = dialog.Formats.Single(f => f.Format == "Letter");

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBe(new ChangePageSizeParams("1-6", 792, 612, PageSizeFit.Center, MatchOrientation: false));
    }

    [Fact]
    public void PageSize_Custom_StartsAtTheCurrentPageSize()
    {
        var dialog = new PageSizeViewModel(2, [1], new PdfSize(842, 595));
        dialog.SelectedFormat = dialog.Formats.Single(f => f.Format is null);

        dialog.IsCustom.ShouldBeTrue();
        (dialog.WidthText, dialog.HeightText).ShouldBe(("210", "297"));

        dialog.WidthText = "100";
        dialog.HeightText = "50";
        dialog.IsPortrait = true;
        dialog.ApplyCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.Width.ShouldBe(50 * PointsPerMillimetre, 1e-9);
        result.Height.ShouldBe(100 * PointsPerMillimetre, 1e-9);
        result.MatchOrientation.ShouldBeFalse();
    }

    [Theory]
    [InlineData("3")]
    [InlineData("6000")]
    [InlineData("wide")]
    public void PageSize_BadCustomSize_IsExplained(string width)
    {
        var dialog = new PageSizeViewModel(2, [], new PdfSize(595, 842)) { WidthText = width };
        dialog.SelectedFormat = dialog.Formats.Single(f => f.Format is null);

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBeNull();
        dialog.Error.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.PageSize_Error_Custom, 7, 5080));
    }

    private static CropPagesViewModel CropDialog(out List<VirtualPage> loads, int[] selected)
    {
        var requested = new List<VirtualPage>();
        loads = requested;
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[4], 4);
        image.Freeze();
        return new CropPagesViewModel(Pages(Enumerable.Repeat(new PdfSize(595, 842), 5).ToArray()), selected, (page, _) =>
        {
            requested.Add(page);
            return Task.FromResult<ImageSource?>(image);
        });
    }

    private static IReadOnlyList<VirtualPage> Pages(params PdfSize[] sizes)
        => VirtualDocuments.Of(new FakePdfDocument(sizes)).Pages;
}
